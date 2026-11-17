using System.Security.Cryptography;
using Hc2000.Asm;
using Hc2000.Machine.Floppy;
using Hc2000.Machine.Storage;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CpNetSprBuilder <repository-root> <output-directory>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var path = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.snios.asm");
var thunkPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.thunk.asm");
var source = File.ReadAllText(path);
var thunk = File.ReadAllText(thunkPath);
const string include = "INCLUDE \"cpnet.thunk.asm\"";
if (!source.Contains(include, StringComparison.Ordinal) || !thunk.Contains("NativeIsland EQU 0xC000", StringComparison.Ordinal))
    throw new InvalidOperationException("CP/NET source no longer has the expected island/include anchors.");
source = source.Replace(include, thunk, StringComparison.Ordinal);
byte[] Assemble(string island, int imageOrigin)
{
    var variant = source.Replace("NativeIsland EQU 0xC000", "NativeIsland EQU " + island, StringComparison.Ordinal);
    var assembly = Assembler.AssembleText(variant, file: path);
    if (!assembly.Success)
        throw new InvalidOperationException(string.Join("; ", assembly.Diagnostics.Select(diagnostic => diagnostic.Message)));
    var image = assembly.ToBinary();
    if (assembly.Segments.Min(segment => segment.Address) != imageOrigin || image.Length != 0x800 ||
        assembly.Symbols["Snios"] != imageOrigin || assembly.Symbols["NativeIsland"] != imageOrigin + 0x300 ||
        assembly.Symbols["DriverStackTop"] > imageOrigin + 0x300)
        throw new InvalidOperationException("CP/NET SNIOS allocation must cover exactly eight pages with five island pages.");
    return image;
}
var zero = Assemble("0x0300", 0);
var one = Assemble("0x0400", 0x100);
var native = Assemble("0xC000", 0xbd00);
var spr = SprBuilder.Create(zero, one);
for (var index = 0; index < zero.Length; index++)
{
    var relocated = spr[256 + index];
    if ((spr[256 + zero.Length + (index >> 3)] & (0x80 >> (index & 7))) != 0)
        relocated = unchecked((byte)(relocated + 0xbd));
    if (relocated != native[index])
        throw new InvalidOperationException($"CP/NET SNIOS native relocation mismatch at {index:X4}.");
}
Directory.CreateDirectory(output);
var sprPath = Path.Combine(output, "SNIOS.SPR");
File.WriteAllBytes(sprPath, spr);
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(path))} {path}");
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(thunkPath))} {thunkPath}");
Console.WriteLine($"SPR {spr.Length} {Hash(spr)} {sprPath} STATIC_ONLY installed=no");

var requesterPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.requester.asm");
var requesterSource = File.ReadAllText(requesterPath);
var requesterIncludes = new[] { "cpnet.request-hooks.inc", "cpnet.request-publish.inc", "cpnet.request-fallback.inc",
    "cpnet.request-session.inc", "cpnet.request-frame.inc", "cpnet.request-select.inc", "cpnet.request-route.inc",
    "cpnet.request-file.inc", "cpnet.request-search.inc", "cpnet.request-search-dispatch.inc",
    "cpnet.sprload.inc", "cpnet.sprread.inc", "cpnet.request-ccp.inc" };
foreach (var name in requesterIncludes)
{
    var includePath = Path.Combine(Path.GetDirectoryName(requesterPath)!, name);
    var directive = $"INCLUDE \"{name}\"";
    if (!requesterSource.Contains(directive, StringComparison.Ordinal))
        throw new InvalidOperationException($"Requester source is missing {directive}.");
    requesterSource = requesterSource.Replace(directive, File.ReadAllText(includePath), StringComparison.Ordinal);
    Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(includePath))} {includePath}");
}
byte[] AssembleRequester(int origin)
{
    var variant = requesterSource.Replace("ORG 0x0000", $"ORG 0x{origin:X4}", StringComparison.Ordinal);
    var assembly = Assembler.AssembleText(variant, file: requesterPath);
    if (!assembly.Success)
        throw new InvalidOperationException(string.Join("; ", assembly.Diagnostics.Select(diagnostic => diagnostic.Message)));
    var image = assembly.ToBinary();
    if (assembly.Segments.Min(segment => segment.Address) != origin || assembly.Symbols["CpNetResidentBase"] != origin ||
        assembly.Symbols["CpNetPublishedBdos"] != origin || origin + image.Length > 0xbd00)
        throw new InvalidOperationException("The independent requester is not contained below SNIOS.");
    Array.Resize(ref image, (image.Length + 255) & ~255);
    return image;
}
var requesterZero = AssembleRequester(0);
var requesterOne = AssembleRequester(0x100);
var requesterSpr = SprBuilder.Create(requesterZero, requesterOne);
var requesterOrigin = 0xbd00 - requesterZero.Length;
var requesterNative = AssembleRequester(requesterOrigin);
for (var index = 0; index < requesterZero.Length; index++)
{
    var relocated = requesterSpr[256 + index];
    if ((requesterSpr[256 + requesterZero.Length + (index >> 3)] & (0x80 >> (index & 7))) != 0)
        relocated = unchecked((byte)(relocated + (requesterOrigin >> 8)));
    if (relocated != requesterNative[index])
        throw new InvalidOperationException($"CP/NET requester native relocation mismatch at {index:X4}.");
}
var requesterSprPath = Path.Combine(output, "NDOS-CANDIDATE.SPR");
File.WriteAllBytes(requesterSprPath, requesterSpr);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(requesterPath))} {requesterPath}");
Console.WriteLine($"SPR {requesterSpr.Length} {Hash(requesterSpr)} {requesterSprPath} STATIC_ONLY installed=no");

var ccpPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.ccp.asm");
var ccpSource = File.ReadAllText(ccpPath);
foreach (var name in new[] { "cpnet.ccp-fcb.inc", "cpnet.ccp-com.inc", "cpnet.ccp-read.inc" })
{
    var includePath = Path.Combine(Path.GetDirectoryName(ccpPath)!, name);
    var directive = $"INCLUDE \"{name}\"";
    if (!ccpSource.Contains(directive, StringComparison.Ordinal))
        throw new InvalidOperationException($"CCP source is missing {directive}.");
    ccpSource = ccpSource.Replace(directive, File.ReadAllText(includePath), StringComparison.Ordinal);
    Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(includePath))} {includePath}");
}
byte[] AssembleCcp(int origin)
{
    var variant = ccpSource.Replace("ORG 0x0000", $"ORG 0x{origin:X4}", StringComparison.Ordinal);
    var assembly = Assembler.AssembleText(variant, file: ccpPath);
    if (!assembly.Success)
        throw new InvalidOperationException(string.Join("; ", assembly.Diagnostics.Select(diagnostic => diagnostic.Message)));
    var image = assembly.ToBinary();
    if (assembly.Segments.Min(segment => segment.Address) != origin || assembly.Symbols["CcpBase"] != origin)
        throw new InvalidOperationException("The independent CCP has an invalid origin.");
    Array.Resize(ref image, (image.Length + 255) & ~255);
    return image;
}
var ccpZero = AssembleCcp(0);
var ccpOne = AssembleCcp(0x100);
var ccpSpr = SprBuilder.Create(ccpZero, ccpOne);
var ccpOrigin = requesterOrigin - ccpZero.Length;
if (ccpOrigin < 0x8c00 + ((ccpSpr.Length + 127) & ~127))
    throw new InvalidOperationException("The candidate CCP destination overlaps its prepared source.");
var ccpNative = AssembleCcp(ccpOrigin);
for (var index = 0; index < ccpZero.Length; index++)
{
    var relocated = ccpSpr[256 + index];
    if ((ccpSpr[256 + ccpZero.Length + (index >> 3)] & (0x80 >> (index & 7))) != 0)
        relocated = unchecked((byte)(relocated + (ccpOrigin >> 8)));
    if (relocated != ccpNative[index])
        throw new InvalidOperationException($"CP/NET CCP native relocation mismatch at {index:X4}.");
}
var ccpSprPath = Path.Combine(output, "CCP-CANDIDATE.SPR");
File.WriteAllBytes(ccpSprPath, ccpSpr);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(ccpPath))} {ccpPath}");
Console.WriteLine($"SPR {ccpSpr.Length} {Hash(ccpSpr)} {ccpSprPath} STATIC_ONLY commands=incomplete installed=no");

var installerPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.install.asm");
var installerSource = File.ReadAllText(installerPath);
foreach (var name in new[] { "cpnet.sprread.inc", "cpnet.sprload.inc", "cpnet.sprlayout.inc", "cpnet.prepare.inc",
    "cpnet.commit.inc", "cpnet.pairvalidate.inc", "cpnet.request-hooks.inc" })
{
    var includePath = Path.Combine(Path.GetDirectoryName(installerPath)!, name);
    var directive = $"INCLUDE \"{name}\"";
    if (!installerSource.Contains(directive, StringComparison.Ordinal))
        throw new InvalidOperationException($"Installer source is missing {directive}.");
    installerSource = installerSource.Replace(directive, File.ReadAllText(includePath), StringComparison.Ordinal);
    Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(includePath))} {includePath}");
}
var installer = Assembler.AssembleText(installerSource, file: installerPath);
if (!installer.Success)
    throw new InvalidOperationException(string.Join("; ", installer.Diagnostics.Select(diagnostic => diagnostic.Message)));
if (installer.Segments.Min(segment => segment.Address) != 0x100 || installer.Symbols["NetInstall"] != 0x100 ||
    installer.Symbols["NetInstallStackTop"] > 0x6000)
    throw new InvalidOperationException("The installer must fit below its first prepared source buffer.");
var installerBinary = installer.ToBinary();
var installerOutput = Path.Combine(output, "NETINST-CANDIDATE.COM");
File.WriteAllBytes(installerOutput, installerBinary);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(installerPath))} {installerPath}");
Console.WriteLine($"COM {installerBinary.Length} {Hash(installerBinary)} {installerOutput} STATIC_ONLY installed=no");

var loginPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.login.asm");
var utilityPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.utility.inc");
var loginSource = File.ReadAllText(loginPath).Replace("INCLUDE \"cpnet.utility.inc\"", File.ReadAllText(utilityPath), StringComparison.Ordinal);
var login = Assembler.AssembleText(loginSource, file: loginPath);
if (!login.Success)
    throw new InvalidOperationException(string.Join("; ", login.Diagnostics.Select(diagnostic => diagnostic.Message)));
if (login.Segments.Min(segment => segment.Address) != 0x100 || login.Symbols["NetLogin"] != 0x100 || login.Symbols["LoginStackTop"] > 0x6000)
    throw new InvalidOperationException("The login utility must fit in transient RAM.");
var loginBinary = login.ToBinary();
var loginOutput = Path.Combine(output, "NETLOGIN-CANDIDATE.COM");
File.WriteAllBytes(loginOutput, loginBinary);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(loginPath))} {loginPath}");
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(utilityPath))} {utilityPath}");
Console.WriteLine($"COM {loginBinary.Length} {Hash(loginBinary)} {loginOutput} STATIC_ONLY installed=no");

var mapPath = Path.Combine(root, "src-library/extended/bios-upgrade/cpnet/cpnet.map.asm");
var mapSource = File.ReadAllText(mapPath).Replace("INCLUDE \"cpnet.utility.inc\"", File.ReadAllText(utilityPath), StringComparison.Ordinal);
var map = Assembler.AssembleText(mapSource, file: mapPath);
if (!map.Success)
    throw new InvalidOperationException(string.Join("; ", map.Diagnostics.Select(diagnostic => diagnostic.Message)));
if (map.Segments.Min(segment => segment.Address) != 0x100 || map.Symbols["NetMap"] != 0x100 || map.Symbols["MapStackTop"] > 0x6000)
    throw new InvalidOperationException("The mapping utility must fit in transient RAM.");
var mapBinary = map.ToBinary();
var mapOutput = Path.Combine(output, "NETMAP-CANDIDATE.COM");
File.WriteAllBytes(mapOutput, mapBinary);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(mapPath))} {mapPath}");
Console.WriteLine($"COM {mapBinary.Length} {Hash(mapBinary)} {mapOutput} STATIC_ONLY installed=no");
var diskSource = Path.Combine(root, "disks/cpm/CPM22-HC.DSK");
var disk = Dsk.Load(File.ReadAllBytes(diskSource));
var filesystem = CpmFs.Open(disk);
filesystem.Put("SNIOS.SPR", spr);
filesystem.Put("NDOS.SPR", requesterSpr);
filesystem.Put("CCP.SPR", ccpSpr);
filesystem.Put("NETINST.COM", installerBinary);
filesystem.Put("NETLOGIN.COM", loginBinary);
filesystem.Put("NETMAP.COM", mapBinary);
var diskBytes = disk.Save();
var diskOutput = Path.Combine(output, "HC2000-CPNET.DSK");
File.WriteAllBytes(diskOutput, diskBytes);
Console.WriteLine($"SOURCE {Hash(File.ReadAllBytes(diskSource))} {diskSource}");
Console.WriteLine($"DISK {diskBytes.Length} {Hash(diskBytes)} {diskOutput} boot-profile=hc2000-v2-cpnet-v1");
return 0;
