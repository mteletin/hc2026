procedure cop(s1,s2:tip1;var r:boolean);
{copy s1 (the current panel's user) to s2: the other panel's user when s2
 is on its drive, the current panel's otherwise. With paged RAM the whole
 file is read into it and then written, in one pass (4 MB at a time for a
 larger one); without, it goes 4 KB at a time through buf.}
var du,k,j,n,cap:integer;b:boolean;
begin
if c<>chr(27) then begin
r:=true;b:=true;
if pgd then cap:=32000 else cap:=32;
if ord(upcase(s2[1]))-65=drv[1-cd] then du:=v[1-cd] else du:=v[cd];
user(v[cd]);
assign(f1,s1);
{$i-}
reset(f1);
{$i+}
if ioresult<>0 then r:=false
else begin
user(du);
assign(f2,s2);
while not(eof(f1)) do begin
k:=0;user(v[cd]);
while not(eof(f1))and(k<cap)do begin
j:=0;while not(eof(f1))and(j<32)do begin
blockread(f1,buf[j*128],1);j:=j+1;end;
if pgd then pmove(k,j,true);
k:=k+j;end;
user(du);
if b then begin rewrite(f2);b:=false;end;
if pgd then begin j:=0;while j<k do begin
n:=k-j;if n>32 then n:=32;
pmove(j,n,false);blockwrite(f2,buf,n);j:=j+n;end;end
else blockwrite(f2,buf,k);end;
if b then begin user(du);rewrite(f2);end;
user(du);close(f2);user(v[cd]);close(f1);
end;end;
end;


procedure copyf;
var s1,s2           :string[40];
    uu,l2,u,l1,st   :integer;
    c               :char;
    r               :boolean;
begin
     paper(2);ink(6);c:=chr(0);
     window(8,10,11,37);l2:=0;
     for u:=1 to 128 do if sl[cd][u]=chr(1) then l2:=1;
     if l2=0 then begin
                  st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
                  sl[cd][st]:=chr(1);end;l1:=0;
     for u:=1 to 128 do
     if sl[cd][u]=chr(1) then
     begin sl[cd][u]:=chr(0);
     gotoxy(12,10);
       lowvideo;
       write('Copy from:');
       highvideo;
s1:=chr(65+drv[cd])+':';
s1:=s1+m[u];s1[11]:='.';
s1:=space(s1);
     write(s1:14);
     gotoxy(12,11);
       lowvideo;
s2:=chr(65+drv[1-cd])+':'+m[u];s2[11]:='.';s2:=space(s2);
       write('       To:');
       write(s2:14);
       highvideo;
if c<>'~' then begin read(kbd,c);
if (c<>'~') then
if ord(c)>29 then begin gotoxy(22,11);write('               ');
     gotoxy(22,11);
     readln(s2);end;end;
if c<>chr(27) then begin
  s2:=space(s2);if s2[2]<>':' then
  s2:=s1[1]+':'+s2;
  cop(s1,s2,r);if c<>chr(27) then l1:=1;
end;end;
if (c<>chr(27))and(l1=1) then begin readf(0);readf(1);end;
list(cv[0],0);list(cv[1],1);
ink(5);paper(1);
end;

procedure movef;
var s1,s2            :string[40];
    uu,l2,u,l1,st    :integer;
    c                :char;
    r                :boolean;
begin
     paper(2);ink(6);c:=chr(0);
     window(8,10,11,37);l2:=0;
     for u:=1 to 128 do if sl[cd][u]=chr(1) then l2:=1;
     if l2=0 then begin
                  st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
                  sl[cd][st]:=chr(1);end;l1:=0;
     for u:=1 to 128 do
     if (sl[cd][u]=chr(1))and((m[u][9]=' ')or(m[u][9]=''))
     then begin
     gotoxy(12,10);sl[cd][u]:=chr(0);
       lowvideo;
       write('Move from:');
       highvideo;
s1:=chr(65+drv[cd])+':'+m[u];s1[11]:='.';
s1:=space(s1);
     write(s1:14);
     gotoxy(12,11);
       lowvideo;
s2:=chr(65+drv[1-cd])+':'+m[u];s2[11]:='.';
s2:=space(s2);
       write('       To:');
       write(s2:14);
       highvideo;
if c<>'~' then begin read(kbd,c);
if (c<>'~')and(c<>chr(27)) then
if ord(c)>29 then begin gotoxy(22,11);write('               ');
     gotoxy(22,11);
     readln(s2);
     end;end;
if c<>chr(27) then begin
s2:=space(s2);
if s2[2]<>':' then
  s2:=s1[1]+':'+s2;
if (upcase(s1[1])=upcase(s2[1]))and((s1[2]=s2[2])and(c<>chr(27)))then begin
  user(v[cd]);assign(f1,s1);rename(f1,s2);l1:=1;end else begin
  cop(s1,s2,r);
  if (r)and(c<>chr(27))then begin user(v[cd]);
  l1:=2;assign(f1,s1);erase(f1);end;
end;end;end;
if (c<>chr(27))and(l1<>0) then begin readf(0);readf(1);end;
list(cv[0],0);list(cv[1],1);
paper(1);ink(5);
end;

procedure erasef;
var f1              :file;
    s1              :string[40];
    uu,l2,u,l1,st,ed:integer;
    c               :char;
begin
     paper(2);ink(6);c:=chr(0);
     window(8,10,10,37);l2:=0;
     for u:=1 to 128 do if sl[cd][u]=chr(1) then l2:=1;
     if l2=0 then begin
                  st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
                  sl[cd][st]:=chr(1);end;l1:=0;
     for u:=1 to 128 do
     if (sl[cd][u]=chr(1))and((m[u][9]=' ')or(m[u][9]=''))
     then begin sl[cd][u]:=chr(0);
     gotoxy(12,10);
       lowvideo;
       write('ERASE :');
       highvideo;
s1:=chr(65+drv[cd])+':';
s1:=s1+m[u];s1[11]:='.';
s1:=space(s1);
     write(s1:14);
if c<>'~' then read(kbd,c);
if (c='~')or(c=chr(13)) then
begin
  user(v[cd]);
  assign(f1,s1);l1:=1;
  if c<>chr(27) then begin user(v[cd]);erase(f1);end;
  if x[cd]>0 then x[cd]:=x[cd]-1 else if a>0 then a:=a-1;
end;
end;
paper(1);ink(5);
if l1=1 then begin readf(cd);
  if (drv[0]=drv[1])and(v[0]=v[1]) then readf(1-cd);end;
list(cv[cd],0);
list(cv[1-cd],1);
paper(1);ink(5);
end;

procedure view;
const ct:integer=13;
var st:integer;
    c :char;
    ln:string[128];
begin
user(v[cd]);
ink(6);paper(0);c:=' ';
gotoxy(1,12);write(#$1b,#$59);k:=nmf;
nmf:=8;list(cv[0],0);list(cv[1],1);nmf:=k;
st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
o:=m[st];o[9]:='.';o:=space(chr(65+drv[cd])+':'+o);
paper(6);ink(2);gotoxy(1,12);write('File:',o);
paper(0);ink(6);assign(ft,o);{$i-} reset(ft);{$i+}
if ioresult=0 then begin
st:=0;while (not(eof(ft)))and(c<>chr(27)) do begin
readln(ft,ln);
if st<10 then begin st:=st+1;
st:=st+(length(ln))div 64;end
else begin
     read(kbd,c);if length(ln)div 64=1 then ct:=12 else ct:=13;
     for i:=1 to 1+length(ln)div 64 do begin
     gotoxy(1,13);
     write(#$1b,#$13);
     end;end;
gotoxy(1,ct+st);for i:=1 to length(ln) do
if (ord(ln[i])<31)or((ord(ln[i])>127)and(ord(ln[i])<159))
then write('.') else
if ord(ln[i])<128 then begin highvideo;write(ln[i]);end
else begin lowvideo;write(ln[i]);end;
end;gotoxy(1,24);lowvideo;
write('Press any key to return...');
read(kbd,c);highvideo;end
else begin gotoxy(1,12);write(#$1b,#$59);gotoxy(1,24);WRITE('ERROR ON VIEWING');
end;
gotoxy(1,12);write(#$1b,#$59);
init;list(cv[0],0);list(cv[1],1);close(ft);
end;

procedure playdem;
var st:integer;
begin
user(v[cd]);
st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
o:=m[st];o[9]:='.';o:=space(chr(65+drv[cd])+':'+o);
assign(ft,o);reset(ft);
clrscr;while not(eof(ft)) do begin
read(ft,c);write(c);end;delay(1000);gotoxy(1,24);
paper(0);ink(5);border(0);iobord(0);
write('Press a key...');read(kbd,c);init;
init;list(cv[0],0);list(cv[1],1);end;

procedure subline(n:integer;o:tip3);
{record n of the batch: the length, the line, a 0 and a $}
var i:integer;
begin
buf[n*128]:=length(o);o:=o+chr(0)+'$';
for i:=1 to length(o) do buf[n*128+i]:=ord(o[i]);
end;

procedure runf;
{run the program under the cursor through a batch, then come back: the
 batch's last record runs first. It is written where FM was started, the
 place the CCP looks for it.}
var st:integer;
    us:string[4];
begin
showk;
for i:=0 to 767 do buf[i]:=26;
subline(0,'FM2');
str(hu,us);subline(1,'USER '+us);
subline(2,hs);
st:=a+y*(nmf+1)+1+x[cd] mod (nmf+1);
o:=m[st];o[9]:=' ';o[10]:=' ';o[11]:=' ';o[12]:=' ';
o:=space(o);
paper(2);ink(6);
window(10,4,12,60);gotoxy(6,11);lowvideo;
write('Command parameters or press ENTER');
gotoxy(6,12);write(chr(65+drv[cd]),'>',o,' ');
readln(cm);o:=o+' '+cm;highvideo;paper(1);
subline(3,o);
clrscr;writeln('Run:',o);
str(v[cd],us);subline(4,'USER '+us);
subline(5,chr(65+drv[cd])+':');
user(hu);
assign(f1,hs+'$$$.SUB');
rewrite(f1);blockwrite(f1,buf,6);close(f1);
assign(ft,hs+'FM001.TMP');rewrite(ft);writeln(ft,drv[0]);
writeln(ft,drv[1]);writeln(ft,nmf);writeln(ft,v[0]);writeln(ft,v[1]);
close(ft);ink(6);paper(1);
bdos(14,hd);mem[4]:=hu*16+hd;halt;end;

