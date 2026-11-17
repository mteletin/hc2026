type tip1=string[20];
     tip3=string[66];
     tip4=string[12];
     tip2=array[1..128]of tip4;

const nmf     :integer=21;

var da              :array[0..1]of tip2;
    m               :tip2;
    ft              :text;
    f1,f2           :file;
    cd,a,nf         :integer;
    drv,x,r,cv,nfd,v:array[0..2]of integer;
    i,y,k           :integer;
    c               :char;
    o,cm            :tip3;
    sl              :array[0..1]of array[1..128]of char;
    buf             :array[0..4095]of byte;  {copy and batch buffer}
    dirb            :array[0..127]of byte;   {directory record of a search}
    pdv             :array[1..16]of integer; {the drives this machine has}
    npd,hd,hu       :integer;                {their count; home drive, user}
    hs              :tip1;                   {home drive as 'X:'}
    pgd             :boolean;                {the machine has paged RAM}
    fn              :array[0..1]of array[0..15]of string[15];
                                             {each panel's drive: its folder names}


procedure set1;var a,b,c:char;begin
a:=chr(27);b:=chr(113);write(a,b);end;
procedure set2;var a,b,c:char;begin
a:=chr(27);b:=chr(112);write(a,b);end;

procedure window(x,y,xx,yy:integer);var k:integer;a:char;
s:string[66];begin if x<xx then if y<yy then begin set2;s:='';
x:=x+1;y:=y+1;xx:=xx+1;yy:=yy+1;
for k:=y+1 to yy-1 do s:=s+' ';gotoxy(y,x);a:=chr(42);
write(a);a:=chr(39);for k:=y+1 to yy-1 do begin gotoxy(k,x);
write(a);gotoxy(k,xx);write(a);end;a:=chr(34);gotoxy(yy,x);
write(a);a:=chr(32);for k:=x+1 to xx-1 do begin gotoxy(y,k);
set2;write(a);set1;write(s);set2;gotoxy(yy,k);write(a);end;
a:=chr(35);gotoxy(y,xx);write(a);a:=chr(41);gotoxy(yy,xx);
write(a);set1;end;end;
procedure ink(x:integer);var a,b,c:char;begin
a:=chr(27);b:=chr(73);c:=chr(48+x);write(a,b,c);end;
procedure paper(x:integer);var a,b,c:char;begin
a:=chr(27);b:=chr(80);c:=chr(48+x);write(a,b,c);end;
procedure iobord(x:integer);var a,b,c:char;begin
a:=chr(27);b:=chr(82);c:=chr(32+x);write(a,b,c);end;
procedure border(x:integer);var a,b,c:char;begin
a:=chr(27);b:=chr(81);c:=chr(48+x);write(a,b,c);end;
procedure hidek;var a,b,c:char;begin
a:=chr(27);b:=chr(117);write(a,b);end;
procedure showk;var a,b,c:char;begin
a:=chr(27);b:=chr(116);write(a,b);end;
procedure clikon;var a,b,c:char;begin
a:=chr(27);b:=chr(114);write(a,b);end;
procedure clikoff;var a,b,c:char;begin
a:=chr(27);b:=chr(115);write(a,b);end;
procedure user(v:byte);
{the user area for the BDOS, and page zero's copy of it for the CCP}
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

procedure pmove(n,cnt:integer;sto:boolean);
{cnt records (32 at most) of the copy store from record n on, from buf to
 the store or from it to buf. The store is paged RAM from page 16384: 128
 records a page, seen at $8000 while its page is mapped. Each half record
 travels through 64 bytes of page zero at $5C (the default FCBs and the
 start of the command tail; Turbo's own run-time code is from $A0), because
 the program's own data and stack may be behind the window: while it is
 open nothing else is touched. The page latches and the interrupt state are left as they were
 found, as the system's own paged code does.}
var p:integer;
begin
p:=16384+n div 128;
mem[$50]:=p mod 256;mem[$51]:=p div 256;
mem[$52]:=(n mod 2)*128;mem[$53]:=$80+(n mod 128) div 2;
mem[$54]:=ord(sto);
{the address of buf, which is negative as an integer above $7FFF}
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

function gate:integer;
{what the paged RAM gate reads: $40 when it is there and closed}
begin
inline($01/$fd/$7c/$ed/$78/$32/$80/$00);
gate:=mem[$80];
end;

procedure adddrv(d:integer);
var g:integer;b:boolean;
begin
b:=true;for g:=1 to npd do if pdv[g]=d then b:=false;
if b then begin npd:=npd+1;pdv[npd]:=d;end;
end;

procedure drives;
{the drives this machine has, asked of its identity record without
 touching a drive: A: and B: always; on an extended HC2000 ("HC2K",
 checksum right) the hard disks from C: and the M: drive. A machine
 without the record is the 1998 one: A: and B:.}
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

procedure init;
begin
ink(7);paper(0);clrscr;border(0);iobord(0);
ink(4);lowvideo;
gotoxy(55,1);write('FM3-MENIU');
gotoxy(55,2);write('1.Drv St.');
gotoxy(55,3);write('2.Drv Dr.');
gotoxy(55,4);write('3.View   ');
{gotoxy(55,5);write('4.Edit   ');}
gotoxy(55,5);write('5.Copy   ');
gotoxy(55,6);write('6.RenMove');
gotoxy(55,7);write('7.Usr.St.');
gotoxy(55,8);write('8.Erase  ');
gotoxy(55,9);write('9.Usr.Dr.');
gotoxy(55,10);write('0.EXIT   ');
paper(1);ink(5);
highvideo;end;

procedure list(a,b:integer);forward;
function chi(c:char):char;
begin
if ord(c)>127 then chi:=chr(ord(c)-128) else chi:=c;
end;

function space(x:tip1):tip1;
var u:integer;g:tip1;
begin
g:='';
for u:=1 to length(x) do
if x[u]<>' ' then g:=g+upcase(x[u]);
space:=g;
end;

procedure readfold(d:integer);
{the folder names of panel d's drive: FOLDERS.CFG in its user 0, "FOLDCFG"
 in record 0 and 16 names of 16 bytes in records 1 and 2. A drive without
 the file has no names, and its user areas show as numbers.}
var g,k:integer;
begin
for g:=0 to 15 do fn[d][g]:='';
user(0);
assign(f1,chr(65+drv[d])+':FOLDERS.CFG');
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
    k:=0;
    while (k<15)and(buf[128+g*16+k]<>0) do begin
      fn[d][g]:=fn[d][g]+chr(buf[128+g*16+k]);k:=k+1;end;
  end;
  close(f1);
end;
end;

procedure usrline(d,u:integer);
{line u of panel d's folder menu: CH, or a user area with its folder name}
var s:string[15];
begin
gotoxy(d*25+10,4+u);
if u=0 then write('CH                ')
else begin
  s:=fn[d][u-1];while length(s)<15 do s:=s+' ';
  write(u-1:2,' ',s);
end;
end;

procedure readf(d:integer);
{panel d's directory: the files of its drive and user, by BDOS search}
var i,g,r,e:integer;o:tip1;ht:char;
begin
i:=0;
for g:=1 to 128 do da[d][g]:='';
bdos(13);readfold(d);bdos(14,drv[d]);user(v[d]);
mem[$5c]:=0;
for g:=1 to 11 do mem[$5c+g]:=ord('?');
for g:=12 to 35 do mem[$5c+g]:=0;
bdos(26,addr(dirb));
r:=bdos(17,$5c);
while (r<4)and(i<128) do begin
  e:=r*32;ht:=' ';
  if dirb[e+10]>128 then ht:=chr(0);
  if dirb[e+9]>127 then if ht<>chr(0) then ht:='*' else ht:='!';
  o:='';
  for g:=1 to 8 do o:=o+chr(dirb[e+g]);
  if ht<>chr(0) then o:=o+ht;
  for g:=9 to 11 do o:=o+chr(dirb[e+g]);
  i:=i+1;da[d][i]:=o;
  r:=bdos(18,0);
end;
nfd[d]:=i;
cv[d]:=0;
x[d]:=0;
end;

function pdi(d:integer):integer;
{where drive d is in the list of drives, 1 if it is not there}
var g,r:integer;
begin
r:=1;for g:=1 to npd do if pdv[g]=d then r:=g;pdi:=r;end;

procedure select(d:integer);
var u,uu,g :integer;
    c      :char;
begin
paper(5);ink(2);k:=1;
window(k,d*28+12,k+npd+2,d*28+15);
gotoxy(d*28+14,k+2);write('CH');
for g:=1 to npd do begin
gotoxy(d*28+14,k+2+g);write(chr(65+pdv[g]),':');end;
u:=pdi(drv[d]);uu:=0;
repeat
gotoxy(d*28+14,k+2+u);lowvideo;
if u=0 then write('CH') else write(chr(65+pdv[u]),':');
highvideo;
if u<>uu then begin
   gotoxy(d*28+14,k+2+uu);
   if uu=0 then write('CH') else write(chr(65+pdv[uu]),':');
end;
read(kbd,c);
if c=chr(24) then if u<npd then begin uu:=u;u:=u+1;end;
if c=chr(5)  then if u>0 then begin uu:=u;u:=u-1;end;
until (c=chr(13))or(c=chr(27));
if c=chr(13) then begin
  if u<>0 then drv[d]:=pdv[u];
  readf(d);
end;
ink(5);paper(1);
end;

procedure selusr(d:integer);
var u,uu :integer;
    c    :char;
begin
paper(5);ink(2);
window(2,d*25+8,20,d*25+27);
for u:=0 to 16 do usrline(d,u);
u:=v[d]+1;uu:=0;
repeat
lowvideo;usrline(d,u);highvideo;
if u<>uu then usrline(d,uu);
read(kbd,c);
if c=chr(24) then if u<16 then begin uu:=u;u:=u+1;end;
if c=chr(5)  then if u>0 then begin uu:=u;u:=u-1;end;
until (c=chr(13))or(c=chr(27));
if c=chr(13) then begin
  if u<>0 then v[d]:=u-1;
  readf(d);
end;
ink(5);paper(1);
end;

procedure list1(a,b:integer);
var i,u  :integer;
    x    :tip2;
    nf   :integer;
begin
paper(1);ink(5);
nf:=nfd[b];
x:=da[b];
if nf>0 then begin

u:=nmf;
if a+u>=nf then begin u:=nf-a-1;
     for i:=2 to nmf+2 do begin
     gotoxy(15+b*27,i);write('            ');end;
     end;
for i:=1 to u+1 do begin
gotoxy(2+b*27,1+i);
if sl[b][a+i]=chr(1) then ink(7) else ink(5);
if length(x[a+i])<13 then write(x[a+i]);end;
for i:=u+2 to nmf+1 do begin
gotoxy(2+b*27,1+i);write('            ');end;


if a+u<nf then
begin
u:=nf-a-nmf+1;
if u>nmf then u:=nmf;
if u>=0 then begin
  for i:=1 to u+1 do begin
  if sl[b][a+i+nmf+1]=chr(1) then ink(7) else ink(5);
  gotoxy(15+b*27,1+i);if length(x[a+i+nmf+1])<13 then write(x[a+i+nmf+1]);end;
  end;
if (u<nmf)and(u>0)then
for i:=u+2 to nmf+1 do begin
gotoxy(15+b*27,1+i);write('            ');end;
end;end;

paper(1);ink(5);end;

procedure list;
var i,u  :integer;
    x    :tip2;
    nf   :integer;
begin
paper(1);ink(5);
x:=da[b];nf:=nfd[b];
window(0,b*27,nmf+2,26+b*27);
gotoxy(b*27+7,1);
lowvideo;
if fn[b][v[b]]<>'' then write(chr(65+drv[b]),':',fn[b][v[b]])
else write(chr(65+drv[b]),':,User',v[b]);
gotoxy(b*27+2,nmf+3);write('Fis:');
write(nfd[b]:3);
highvideo;
list1(a,b);
end;

procedure desen(d:integer);
var nf,xx,yy,a:integer;
    m         :tip2;
begin
xx:=x[d];
d:=d mod 2;
a:=cv[d];
m:=da[d];
nf:=nfd[d];
if nf<1 then begin
gotoxy(2+d*27,2);x[d]:=0;y:=0;write('            ');
highvideo;end;

if nf>1 then begin
if c=chr(4) then
     if (x[d]<=nmf)then
     begin xx:=x[d];x[d]:=x[d]+nmf+1;
           if not(x[d]+2+a<nf)then x[d]:=nf-a-1;
     end
     else if nf>a+nmf*2+2 then
                        begin
                        a:=a+nmf*2+2;
                        if a+x[d]+2>nf then x[d]:=nf-a-1;
                        xx:=1;
                        list1(a,d);cv[d]:=a;end;
if c=chr(19) then
     if x[d]>nmf then begin
                      xx:=x[d];
                      x[d]:=x[d]-nmf-1;
                      end
     else
     if a>2*nmf+2 then
            begin
            a:=a-2*nmf-2;
            list1(a,d);end else
                           begin
                           if a=0 then x[d]:=0;
                           a:=0;xx:=1;
                           list1(a,d);
                           cv[d]:=a;
                           end;

if c=chr(24) then
if(nmf*2+1>x[d])and(nf>x[d]+a+1)then begin
xx:=x[d];x[d]:=x[d]+1;end
else
     if nf>a+nmf*2+2 then begin
                        a:=a+1;
                        list1(a,d);end;
if c=chr(5) then
if x[d]>0 then begin
   xx:=x[d];
   x[d]:=x[d]-1;end
else begin x[d]:=0;
     if a>0 then
            begin
            a:=a-1;
            list1(a,d);end else a:=0;
     end;
cv[d]:=a;
y:=x[d] div (nmf+1);
yy:=xx div (nmf+1);
if xx<>x[d] then begin
if sl[d][a+xx+1]=chr(1) then ink(7)
else ink(5);
gotoxy(2+yy*13+d*27,2+xx mod (nmf+1));
highvideo;write(m[a+xx+1]);
cv[d]:=a;
end;

if sl[d][a+x[cd]+1]=chr(1) then ink(7)
else ink(5);
if c<>chr(9) then begin
 gotoxy(2+y*13+d*27,2+x[d] mod (nmf+1));
 lowvideo;write(m[a+x[d]+1]);highvideo;
 ink(5);end;
end;end;

