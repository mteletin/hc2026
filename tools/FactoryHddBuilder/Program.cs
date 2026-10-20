using Hc2000.HostFiles;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: FactoryHddBuilder <output-directory>");
    return 2;
}

HddFactoryImages.GenerateInto(args[0], overwrite: true);
Console.Write(File.ReadAllText(Path.Combine(args[0], HddFactoryImages.ReceiptName)));
Console.WriteLine("FACTORY_HDD HCVD D=D.DSK E=E.DSK F=F.DSK total_image_bytes=25165824 profile=hc2000-cpm22-large-drive-dpb-v1 C=not-supplied");
return 0;
