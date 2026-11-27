{FM5A: the machine, the screen and the directory store of FM5 (hc2000-fm5).}
type tip1=string[20];
     tip3=string[80];
     tip4=string[12];
     tipf=string[16];
     uarr=array[0..15]of boolean;

const maxe    :integer=1024;   {entries a panel holds: '..', the folders, the files}
      dirpage :integer=16896;  {paged RAM page of panel 0's directory ($4200); panel 1's is the next}

var sw,sh,pw,ncol,nrow,cw  :integer;  {screen, panel width, name columns and rows a panel, column width}
    oncard                 :boolean;  {the console is the X2 card}
    attr0                  :integer;  {the console's attribute when FM started}
    drv,usr,cnt,top,cur    :array[0..1]of integer;
    idx                    :array[0..1]of array[1..1024]of integer;
    mk                     :array[0..1]of array[1..1024]of boolean;
    fn                     :array[0..1]of array[0..15]of tipf;
    hascfg                 :array[0..1]of boolean;  {the panel's drive has FOLDERS.CFG}
    ent                    :array[0..15]of byte;  {one directory entry: kind, user, name 11, flags}
    buf                    :array[0..4095]of byte;
    dirb                   :array[0..127]of byte;
    pdv                    :array[1..16]of integer;
    npd,hd,hu,cd,i,k       :integer;
    hs                     :tip1;
    pgd                    :boolean;
    c                      :char;
    f1,f2                  :file;
    ft                     :text;
    o,cm                   :tip3;
    nfo                    :array[0..1]of integer;  {folders (and '..') at the head of each panel}
    full                   :array[0..1]of boolean;  {the folder holds more files than the panel shows}
    dx,dy,dw               :integer;                {the dialog's frame}
    sel                    :array[1..1024]of integer;
    nsel                   :integer;
    outer,quit             :boolean;                {a batch FM was started from; Q pressed}

{---------------------------------------------------------------------------
 The console: hc-term codes.}
procedure esc2(b:integer);begin write(chr(27),chr(b));end;
procedure ink(x:integer);begin write(chr(27),'I',chr(48+x));end;
procedure paper(x:integer);begin write(chr(27),'P',chr(48+x));end;
procedure shapes(on:boolean);begin if on then esc2(112) else esc2(113);end;
procedure hidek;begin esc2(117);end;
procedure showk;begin esc2(116);end;
procedure clikoff;begin esc2(115);end;
procedure clikon;begin esc2(114);end;
procedure at(x,y:integer);begin gotoxy(x,y);end;

{n spaces}
procedure spaces(n:integer);var g:integer;begin for g:=1 to n do write(' ');end;

{a frame from column x, row y, w wide and h high, filled with the current paper}
procedure frame(x,y,w,h:integer);
var g:integer;
begin
at(x,y);shapes(true);write('*');for g:=1 to w-2 do write('''');write('"');
for g:=1 to h-2 do begin
  at(x,y+g);write(' ');shapes(false);spaces(w-2);shapes(true);write(' ');end;
at(x,y+h-1);write('#');for g:=1 to w-2 do write('''');write(')');shapes(false);
end;

{---------------------------------------------------------------------------
 The machine.}
procedure user(v:integer);
{the user area (folder) for the BDOS, and page zero's copy of it for the CCP}
begin
bdos(32,v);
mem[4]:=(mem[4] mod 16)+v*16;end;

function idb(n:integer):integer;
{byte n of the machine identity record: port $609D + n*256}
begin
mem[$81]:=$60+n;
inline($3a/$81/$00/$47/$0e/$9d/$ed/$78/$32/$80/$00);
idb:=mem[$80];
end;

function gate:integer;
{what the paged RAM gate reads: $40 when it is there and closed}
begin
inline($01/$fd/$7c/$ed/$78/$32/$80/$00);
gate:=mem[$80];
end;

procedure pmove(n,cnt:integer;sto:boolean);
{cnt records (32 at most) of the copy store from record n on, from buf to
 the store or from it to buf (FM2's routine). The store is paged RAM from
 page 16384: 128 records a page, seen at $8000 while its page is mapped.
 Each half record travels through 64 bytes of page zero at $5C, because the
 program's own data and stack may be behind the window. The page latches and
 the interrupt state are left as they were found.}
var p:integer;
begin
p:=16384+n div 128;
mem[$50]:=p mod 256;mem[$51]:=p div 256;
mem[$52]:=(n mod 2)*128;mem[$53]:=$80+(n mod 128) div 2;
mem[$54]:=ord(sto);
p:=addr(buf);mem[$55]:=p and 255;mem[$56]:=((p-(p and 255)) div 256) and 255;
mem[$59]:=cnt*2;
inline($ed/$57/$f5/$f3);
inline($01/$fd/$7e/$ed/$78/$32/$57/$00/$06/$7d/$ed/$78/$32/$58/$00);
inline($3a/$54/$00/$b7/$28/$0b);
inline($2a/$55/$00/$11/$5c/$00/$01/$40/$00/$ed/$b0);
inline($2a/$50/$00/$01/$fd/$7e/$ed/$69/$06/$7d/$ed/$61/$06/$7c/$3e/$01/$ed/$79);
inline($ed/$5b/$52/$00/$21/$5c/$00/$3a/$54/$00/$b7/$20/$01/$eb);
inline($01/$40/$00/$ed/$b0);
inline($01/$fd/$7c/$af/$ed/$79);
inline($3a/$54/$00/$b7/$20/$0c);
inline($21/$5c/$00/$ed/$5b/$55/$00/$01/$40/$00/$ed/$b0);
inline($2a/$55/$00/$11/$40/$00/$19/$22/$55/$00);
inline($2a/$52/$00/$19/$22/$52/$00);
inline($21/$59/$00/$35/$20/$9b);
inline($3a/$57/$00/$01/$fd/$7e/$ed/$79/$3a/$58/$00/$06/$7d/$ed/$79);
inline($c1/$cb/$51/$28/$01/$fb);
end;

procedure pent(p,n:integer;sto:boolean);
{entry n (1-1024) of panel p's directory store, 16 bytes at $8000+(n-1)*16 of
 page dirpage+p, to or from ent, through page zero at $5C as pmove does.}
var a:integer;
begin
if sto then for a:=0 to 15 do mem[$5c+a]:=ent[a];
a:=dirpage+p;mem[$50]:=a mod 256;mem[$51]:=a div 256;
a:=(n-1)*16;mem[$52]:=a mod 256;mem[$53]:=$80+a div 256;
mem[$54]:=ord(sto);
inline($ed/$57/$f5/$f3);
inline($01/$fd/$7e/$ed/$78/$32/$57/$00/$06/$7d/$ed/$78/$32/$58/$00);
inline($2a/$50/$00/$01/$fd/$7e/$ed/$69/$06/$7d/$ed/$61/$06/$7c/$3e/$01/$ed/$79);
inline($ed/$5b/$52/$00/$21/$5c/$00/$3a/$54/$00/$b7/$20/$01/$eb);
inline($01/$10/$00/$ed/$b0);
inline($01/$fd/$7c/$af/$ed/$79);
inline($3a/$57/$00/$01/$fd/$7e/$ed/$79/$3a/$58/$00/$06/$7d/$ed/$79);
inline($c1/$cb/$51/$28/$01/$fb);
if not sto then for a:=0 to 15 do ent[a]:=mem[$5c+a];
end;

procedure adddrv(d:integer);
var g:integer;b:boolean;
begin
b:=true;for g:=1 to npd do if pdv[g]=d then b:=false;
if b then begin npd:=npd+1;pdv[npd]:=d;end;
end;

procedure drives;
{the drives this machine has, from its identity record: A: and B:, the hard
 disks from C: and the M: drive; and whether it has paged RAM}
var g,s:integer;id:array[0..15]of integer;
begin
npd:=0;adddrv(0);adddrv(1);pgd:=false;
s:=0;for g:=0 to 15 do begin id[g]:=idb(g);if g<15 then s:=s+id[g];end;
if (id[0]=72)and(id[1]=67)and(id[2]=50)and(id[3]=75)and(id[15]=s mod 256)
then begin
  if (id[7] and 1)<>0 then for g:=1 to id[11] do adddrv(1+g);
  if (id[7] and 4)<>0 then adddrv(12);
  pgd:=gate=$40;
end;
adddrv(hd);
end;

function hasdrv(d:integer):boolean;
var g:integer;b:boolean;
begin
b:=false;for g:=1 to npd do if pdv[g]=d then b:=true;hasdrv:=b;end;

procedure screen;
{the console's size: the card's console receipt ($B3 on the selector port
 $779D: tag, version, row, column, columns, rows, attribute) when CON: is the
 card, else the ROM console's 64 x 24 and its attribute at DB0C of the video
 bank (switched in with OUT C5 and back with OUT C7, interrupts off, nothing
 on the stack while it is in).}
begin
oncard:=(mem[3] and 3)=3;
sw:=64;sh:=24;attr0:=$44;
if oncard then begin
  inline($01/$9d/$77/$3e/$b3/$ed/$79/
         $ed/$78/$32/$80/$00/$ed/$78/$32/$81/$00/$ed/$78/$ed/$78/
         $ed/$78/$32/$82/$00/$ed/$78/$32/$83/$00/
         $ed/$78/$32/$84/$00/$ed/$78/$32/$85/$00/
         $ed/$78/$32/$86/$00);
  if mem[$80]=$b3 then begin
    sw:=mem[$82]+256*mem[$83];sh:=mem[$84]+256*mem[$85];attr0:=mem[$86];end;
end else begin
  inline($f3/$3e/$ee/$d3/$c5/$3a/$0c/$db/$32/$80/$00/$3e/$ee/$d3/$c7/$fb);
  attr0:=mem[$80];
end;
if sw>160 then sw:=160;if sh>64 then sh:=64;
if sw<40 then sw:=40;if sh<20 then sh:=20;
pw:=sw div 2;
ncol:=(pw-2) div 13;if ncol<1 then ncol:=1;
cw:=(pw-2) div ncol;
nrow:=sh-4;
end;

{---------------------------------------------------------------------------
 Folders: user areas named in FOLDERS.CFG (user 0 of the drive), the names
 with a leading '/'; a drive without the file has the shell's defaults.}
function defname(u:integer):tipf;
{the shell's name for folder u of a drive without FOLDERS.CFG}
var s:string[4];
begin
str(u,s);defname:='/u'+s;
if u=0 then defname:='/';if u=1 then defname:='/bin';if u=2 then defname:='/etc';
if u=3 then defname:='/src';if u=4 then defname:='/opt';if u=5 then defname:='/sbin';
end;

procedure readfold(d:integer);
var g,k:integer;
begin
hascfg[d]:=false;
for g:=0 to 15 do fn[d][g]:=defname(g);
bdos(14,drv[d]);user(0);
assign(f1,'FOLDERS.CFG');
{$i-}
reset(f1);
{$i+}
if ioresult=0 then begin
  for g:=0 to 383 do buf[g]:=0;
  {$i-}
  blockread(f1,buf,3);
  {$i+}
  if ioresult=0 then
  if (buf[0]=70)and(buf[1]=79)and(buf[2]=76)and(buf[3]=68)and(buf[4]=67) then
  for g:=0 to 15 do begin
    hascfg[d]:=true;
    fn[d][g]:='';k:=0;
    while (k<16)and(buf[128+g*16+k]<>0) do begin
      fn[d][g]:=fn[d][g]+chr(buf[128+g*16+k]);k:=k+1;end;
  end;
  close(f1);
end;
end;

function parent(s:tipf):tipf;
{'/prog/fm' -> '/prog', '/bin' -> '/', '/' -> ''}
var g:integer;
begin
if s='/' then parent:=''
else begin
  g:=length(s);while (g>1)and(s[g]<>'/') do g:=g-1;
  parent:=copy(s,1,g-1);if g=1 then parent:='/';
end;
end;

function leaf(s:tipf):tipf;
var g:integer;
begin
g:=length(s);while (g>1)and(s[g-1]<>'/') do g:=g-1;
leaf:=copy(s,g,length(s)-g+1);
end;

function findfold(d:integer;s:tipf):integer;
var g,r:integer;
begin
r:=-1;for g:=0 to 15 do if (r<0)and(fn[d][g]=s) then r:=g;findfold:=r;end;

{---------------------------------------------------------------------------
 The directory of panel p: its folder's subfolders (and '..'), then its
 files, sorted, in the paged store; idx holds the order.}
function entname(p,n:integer):tip4;
{entry n of panel p as shown: 'NAME.EXT', or a folder's last name and '/'}
var g:integer;s:tip4;
begin
pent(p,n,false);
if ent[0]=2 then entname:='..'
else if ent[0]=1 then entname:=copy(leaf(fn[p][ent[1]]),1,11)+'/'
else begin
  s:='';
  for g:=2 to 9 do if ent[g]<>32 then s:=s+chr(ent[g]);
  if (ent[10]<>32)or(ent[11]<>32)or(ent[12]<>32) then begin
    s:=s+'.';for g:=10 to 12 do if ent[g]<>32 then s:=s+chr(ent[g]);end;
  entname:=s;
end;
end;

function pgt(p,a,b:integer):boolean;
{entry a of panel p sorts after entry b: '..' first, then the folders, then
 the files, each by name. Both are compared in place in the paged window
 ($8000 of page dirpage+p, mapped as pent maps it): the kind, then the 11
 name bytes (a folder's entry holds its name there too).}
var t:integer;
begin
t:=dirpage+p;mem[$50]:=t mod 256;mem[$51]:=t div 256;
t:=(a-1)*16;mem[$52]:=t mod 256;mem[$53]:=$80+t div 256;
t:=(b-1)*16;mem[$54]:=t mod 256;mem[$55]:=$80+t div 256;
inline($ed/$57/$f5/$f3);
inline($01/$fd/$7e/$ed/$78/$32/$57/$00/$06/$7d/$ed/$78/$32/$58/$00);
inline($2a/$50/$00/$01/$fd/$7e/$ed/$69/$06/$7d/$ed/$61/$06/$7c/$3e/$01/$ed/$79);
{HL=a, DE=b; kinds differ: a after b when its kind is lower (2 '..', 1
 folder, 0 file); else the first name byte that differs; $56 = 1 or 0}
inline($2a/$52/$00/$ed/$5b/$54/$00/$1a/$4f/$7e/$b9/$28/$05/$3e/$00/$17/$18/$15);
inline($23/$13/$06/$0b/$23/$13/$1a/$4f/$7e/$b9/$20/$05/$10/$f6/$af/$18/$04);
inline($3e/$00/$3f/$17/$32/$56/$00);
inline($01/$fd/$7c/$af/$ed/$79);
inline($3a/$57/$00/$01/$fd/$7e/$ed/$79/$3a/$58/$00/$06/$7d/$ed/$79);
inline($c1/$cb/$51/$28/$01/$fb);
pgt:=mem[$56]<>0;
end;

procedure sortp(p:integer);
{shell sort of idx, entries compared in place by pgt}
var gap,a,b,t:integer;done:boolean;
begin
gap:=cnt[p] div 2;
while gap>0 do begin
  for a:=gap+1 to cnt[p] do begin
    b:=a;done:=false;
    while (b>gap)and not done do begin
      if pgt(p,idx[p][b-gap],idx[p][b]) then begin
        t:=idx[p][b];idx[p][b]:=idx[p][b-gap];idx[p][b-gap]:=t;b:=b-gap;end
      else done:=true;
    end;
  end;
  gap:=gap div 2;
end;
end;

procedure addent(p:integer);
begin
if cnt[p]<maxe then begin cnt[p]:=cnt[p]+1;pent(p,cnt[p],true);idx[p][cnt[p]]:=cnt[p];
  mk[p][cnt[p]]:=false;end;
end;

procedure usedusers(var used:uarr);
{the folders (users) of the current drive with any file in them: one pass
 over the whole directory, a search with '?' for the drive byte}
var r,u:integer;
begin
for u:=0 to 15 do used[u]:=false;
mem[$5c]:=ord('?');for k:=1 to 11 do mem[$5c+k]:=ord('?');for k:=12 to 35 do mem[$5c+k]:=0;
bdos(26,addr(dirb));
r:=bdos(17,$5c);
while r<4 do begin u:=dirb[r*32];if u<16 then used[u]:=true;r:=bdos(18,0);end;
end;

procedure readp(p:integer);
var g,r,e,u:integer;cfg,scanned:boolean;s,nm:tipf;used:uarr;
begin
cnt[p]:=0;top[p]:=0;cur[p]:=1;
{a floppy may have been changed: only A: and B: are reset (BDOS 37); a reset
 of every drive would read each hard disk's whole directory again}
if drv[p]<2 then bdos(37,1+drv[p]);
readfold(p);bdos(14,drv[p]);
cfg:=hascfg[p];
s:=fn[p][usr[p]];
if s='' then s:=defname(usr[p]);
if (s<>'/')and(s<>'') then begin
  for g:=0 to 15 do ent[g]:=0;ent[0]:=2;u:=findfold(p,parent(s));if u<0 then u:=0;ent[1]:=u;addent(p);
end;
scanned:=false;
for u:=0 to 15 do
  if (u<>usr[p])and(fn[p][u]<>'')and(parent(fn[p][u])=s) then begin
  {the shell's placeholder names (/u6 to /u15, and every folder of a drive
   without FOLDERS.CFG) show only when the folder holds files}
  if not(cfg and((u<6)or(fn[p][u]<>defname(u)))) and not scanned then begin usedusers(used);scanned:=true;end;
  if (cfg and((u<6)or(fn[p][u]<>defname(u)))) or used[u] then begin
    for g:=0 to 15 do ent[g]:=0;ent[0]:=1;ent[1]:=u;
    nm:=leaf(fn[p][u]);for g:=1 to length(nm) do if g<=11 then ent[g+1]:=ord(upcase(nm[g]));
    addent(p);end;
  end;
nfo[p]:=cnt[p];
user(usr[p]);
mem[$5c]:=0;
for g:=1 to 11 do mem[$5c+g]:=ord('?');
for g:=12 to 35 do mem[$5c+g]:=0;
bdos(26,addr(dirb));
r:=bdos(17,$5c);
while (r<4)and(cnt[p]<maxe) do begin
  e:=r*32;
  for g:=0 to 15 do ent[g]:=0;
  for g:=1 to 11 do ent[g+1]:=dirb[e+g] and $7f;
  if dirb[e+9]>127 then ent[13]:=ent[13] or 1;   {read-only}
  if dirb[e+10]>127 then ent[13]:=ent[13] or 2;  {system}
  if dirb[e+11]>127 then ent[13]:=ent[13] or 4;  {archived}
  addent(p);
  r:=bdos(18,0);
end;
{a directory entry left over: the panel is full and says so}
full[p]:=r<4;
sortp(p);
end;

{the file name of entry n of panel p for assign: d:NAME.EXT}
function fname(p,n:integer):tip1;
var s:tip1;g:integer;
begin
pent(p,n,false);
s:=chr(65+drv[p])+':';
for g:=2 to 9 do if ent[g]<>32 then s:=s+chr(ent[g]);
if (ent[10]<>32)or(ent[11]<>32)or(ent[12]<>32) then begin
  s:=s+'.';for g:=10 to 12 do if ent[g]<>32 then s:=s+chr(ent[g]);end;
fname:=s;
end;

{the folder of panel p as a title: C:/prog/fm}
function title(p:integer):tip3;
var s:tipf;
begin
s:=fn[p][usr[p]];if s='' then s:=defname(usr[p]);
title:=chr(65+drv[p])+':'+s;
end;
