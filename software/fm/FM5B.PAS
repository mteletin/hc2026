{FM5B: drawing, dialogs and the operations of FM5 (hc2000-fm5).}

function px(p:integer):integer;begin px:=1+p*pw;end;
function pwid(p:integer):integer;begin if p=0 then pwid:=pw else pwid:=sw-pw;end;

procedure drawent(p,n:integer);
{the n-th entry in panel p's order, where it is on the screen}
var r,x,y:integer;s:tip4;
begin
if (n>top[p])and(n<=top[p]+ncol*nrow)and(n<=cnt[p]) then begin
  r:=n-top[p]-1;x:=px(p)+1+(r div nrow)*cw;y:=2+r mod nrow;
  s:=entname(p,idx[p][n]);
  paper(1);
  if ent[0]<>0 then ink(6) else if mk[p][idx[p][n]] then ink(4)
  else if (ent[13] and 2)<>0 then ink(5) else ink(7);
  at(x,y);
  if (p=cd)and(n=cur[p]) then lowvideo;
  write(s);spaces(cw-1-length(s));
  highvideo;
end;
end;

procedure drawlist(p:integer);
var n,r,x,y:integer;
begin
for n:=top[p]+1 to top[p]+ncol*nrow do
  if n<=cnt[p] then drawent(p,n)
  else begin
    r:=n-top[p]-1;x:=px(p)+1+(r div nrow)*cw;y:=2+r mod nrow;
    paper(1);at(x,y);spaces(cw-1);end;
end;

procedure drawfoot(p:integer);
var n,f,m:integer;
begin
f:=cnt[p]-nfo[p];m:=0;
for n:=1 to cnt[p] do begin if mk[p][n] then m:=m+1;end;
paper(1);ink(5);at(px(p)+2,sh-2);
write(' ',f);if full[p] then write('+');write(' files');if m>0 then write(', ',m,' marked');write(' ');
end;

procedure drawpanel(p:integer);
var t:tip3;
begin
paper(1);ink(5);
frame(px(p),1,pwid(p),sh-2);
t:=title(p);if length(t)>pwid(p)-4 then t:=copy(t,1,pwid(p)-4);
at(px(p)+2,1);if p=cd then lowvideo;write(' ',t,' ');highvideo;
drawlist(p);drawfoot(p);
end;

procedure keybar;
var s:tip3;narrow:boolean;
begin
narrow:=false;
paper(0);ink(6);at(1,sh);
if sw>=80 then s:='C Copy  M Move  R Rename  E Erase  V View  D Drive  Space Mark  A All  Q Quit'
else if sw>=60 then s:='C Copy M Move R Ren E Erase V View D Drive A All Q Quit'
else begin
  {40 columns: each command's word, its key the first letter, in yellow}
  s:='Copy Move Ren Erase View Drv All Quit';
  for k:=1 to length(s) do begin
    if (k=1) or (s[k-1]=' ') then ink(6) else ink(7);
    write(s[k]);
  end;
  spaces(sw-1-length(s));
  narrow:=true;
end;
if not narrow then begin
  if length(s)>sw-1 then s:=copy(s,1,sw-1);
  write(s);spaces(sw-1-length(s));
end;
end;

procedure status;
{the current entry: its name, size and flags}
var s:tip3;n,r:integer;
begin
paper(0);ink(7);at(1,sh-1);s:='';
if cnt[cd]>0 then begin
  n:=idx[cd][cur[cd]];s:=entname(cd,n);
  if ent[0]=0 then begin
    o:=fname(cd,n);
    user(usr[cd]);
    mem[$5c]:=drv[cd]+1;for k:=1 to 11 do mem[$5c+k]:=ent[k+1];for k:=12 to 35 do mem[$5c+k]:=0;
    bdos(35,$5c);
    {the size in records, as kilobytes: 8 records a kilobyte}
    r:=mem[$5c+33] div 8+32*mem[$5c+34]+8192*mem[$5c+35];
    if (mem[$5c+33] mod 8)<>0 then r:=r+1;
    str(r,o);
    s:=s+'  '+o+'K';
    if (ent[13] and 1)<>0 then s:=s+'  read-only';
    if (ent[13] and 2)<>0 then s:=s+'  system';
  end else if ent[0]=1 then s:=s+'  folder '+title(cd)+leaf(fn[cd][ent[1]])
  else s:=s+'  up';
end;
if length(s)>sw-1 then s:=copy(s,1,sw-1);
write(s);spaces(sw-1-length(s));
end;

procedure drawall;
begin
paper(0);ink(7);clrscr;
drawpanel(0);drawpanel(1);keybar;status;
end;

{cursor of panel p moved to n: scrolled to stay on screen}
procedure moveto(p,n:integer);
var o,v:integer;
begin
if n>cnt[p] then n:=cnt[p];if n<1 then n:=1;
o:=cur[p];cur[p]:=n;v:=ncol*nrow;
if (n<=top[p])or(n>top[p]+v) then begin
  if n<=top[p] then top[p]:=((n-1) div nrow)*nrow
  else top[p]:=((n-1) div nrow-ncol+1)*nrow;
  if top[p]<0 then top[p]:=0;
  drawlist(p);
end else begin drawent(p,o);drawent(p,n);end;
status;
end;

{---------------------------------------------------------------------------
 Dialogs in a frame in the middle of the screen.}

procedure dialog(h:integer;t:tip3);
begin
dw:=sw-8;if dw>60 then dw:=60;
dx:=(sw-dw) div 2+1;dy:=(sh-h) div 2;
paper(5);ink(0);frame(dx,dy,dw,h);
if length(t)>dw-4 then t:=copy(t,1,dw-4);
at(dx+2,dy);write(' ',t,' ');
end;

procedure dline(n:integer;s:tip3);
begin
paper(5);ink(0);at(dx+2,dy+n);
if length(s)>dw-4 then s:=copy(s,1,dw-4);
write(s);spaces(dw-4-length(s));
end;

function yesno(n:integer;s:tip3):char;
{Y, N or Esc (as chr(27)); A as well when s offers it}
var r:char;
begin
dline(n,s);
repeat read(kbd,r);r:=upcase(r);
until (r='Y')or(r='N')or(r=chr(27))or((r='A')and(pos('(A)',s)>0));
yesno:=r;
end;

function editline(n,max:integer;l:tip3;var s:tip3):boolean;
{a line of at most max characters typed after label l, starting from s;
 false on Esc}
var r:char;done,ok:boolean;
begin
done:=false;ok:=false;showk;
repeat
  dline(n,l+s);at(dx+2+length(l)+length(s),dy+n);
  read(kbd,r);
  if r=chr(13) then begin done:=true;ok:=true;end
  else if r=chr(27) then done:=true
  else if (r=chr(8))or(r=chr(127)) then begin if length(s)>0 then s:=copy(s,1,length(s)-1);end
  else if (r>=' ')and(r<chr(127))and(length(s)<max) then s:=s+upcase(r);
until done;
hidek;editline:=ok;
end;

{---------------------------------------------------------------------------
 Files by FCB at $5C: the name 'D:NAME.EXT', its user.}
procedure mkfcb(s:tip1);
var g,j:integer;
begin
mem[$5c]:=ord(s[1])-64;for g:=1 to 11 do mem[$5c+g]:=32;for g:=12 to 35 do mem[$5c+g]:=0;
g:=3;j:=1;
while (g<=length(s))and(s[g]<>'.')and(j<=8) do begin mem[$5c+j]:=ord(upcase(s[g]));g:=g+1;j:=j+1;end;
while (g<=length(s))and(s[g]<>'.') do g:=g+1;
g:=g+1;j:=9;
while (g<=length(s))and(j<=11) do begin mem[$5c+j]:=ord(upcase(s[g]));g:=g+1;j:=j+1;end;
end;

function flagsof(s:tip1;u:integer):integer;
{-1 when the file is not there, else its flags: 1 read-only, 2 system, 4 archived}
var r,e,f:integer;
begin
mkfcb(s);user(u);bdos(26,addr(dirb));
r:=bdos(17,$5c);
if r>3 then flagsof:=-1
else begin
  e:=r*32;f:=0;
  if dirb[e+9]>127 then f:=f or 1;if dirb[e+10]>127 then f:=f or 2;if dirb[e+11]>127 then f:=f or 4;
  flagsof:=f;
end;
end;

function goodname(s:tip1):boolean;
{a CP/M file name: 1 to 8 characters, at most one dot and 3 after it, none of
 the characters CP/M keeps for itself}
var g,d,b,e:integer;ok:boolean;
begin
ok:=true;d:=0;b:=0;e:=0;
for g:=1 to length(s) do
  if s[g]='.' then d:=d+1
  else begin
    if pos(s[g],'*?:,;<>=[]|/')>0 then ok:=false;
    if d=0 then b:=b+1 else e:=e+1;
  end;
goodname:=ok and(d<=1)and(b>=1)and(b<=8)and(e<=3);
end;

procedure setflags(s:tip1;u,f:integer);
begin
mkfcb(s);
if (f and 1)<>0 then mem[$5c+9]:=mem[$5c+9] or 128;
if (f and 2)<>0 then mem[$5c+10]:=mem[$5c+10] or 128;
if (f and 4)<>0 then mem[$5c+11]:=mem[$5c+11] or 128;
user(u);bdos(30,$5c);
end;

procedure delfile(s:tip1;u:integer);
{erase a file, its read-only flag taken off first (CP/M stops a program that
 erases a read-only file)}
begin
setflags(s,u,0);mkfcb(s);user(u);bdos(19,$5c);
end;

function samesize(s1:tip1;u1:integer;s2:tip1;u2:integer):boolean;
{the two files' sizes in records (BDOS 35), all three bytes}
var a0,a1,a2:integer;
begin
mkfcb(s1);user(u1);bdos(35,$5c);a0:=mem[$5c+33];a1:=mem[$5c+34];a2:=mem[$5c+35];
mkfcb(s2);user(u2);bdos(35,$5c);
samesize:=(a0=mem[$5c+33])and(a1=mem[$5c+34])and(a2=mem[$5c+35]);
end;

procedure cop(s1:tip1;su:integer;s2:tip1;du:integer;var ok:boolean);
{copy s1 in folder su to s2 in folder du, its flags kept. With paged RAM the
 whole file is read into it and then written, 4 MB at a time (FM2's way);
 without, 4 KB at a time through buf.}
var k,j,n,cap,fl:integer;b:boolean;
begin
ok:=true;b:=true;
if pgd then cap:=32000 else cap:=32;
fl:=flagsof(s1,su);
if flagsof(s2,du)>=0 then setflags(s2,du,0);
user(su);assign(f1,s1);
{$i-}
reset(f1);
if ioresult<>0 then ok:=false
else begin
  assign(f2,s2);
  while ok and not(eof(f1)) do begin
    k:=0;user(su);
    while ok and not(eof(f1))and(k<cap)do begin
      j:=0;while not(eof(f1))and(j<32)do begin blockread(f1,buf[j*128],1);j:=j+1;end;
      if ioresult<>0 then ok:=false;
      if pgd then pmove(k,j,true);
      k:=k+j;end;
    user(du);
    if ok and b then begin rewrite(f2);b:=false;if ioresult<>0 then ok:=false;end;
    if ok then
      if pgd then begin j:=0;while ok and(j<k) do begin
        n:=k-j;if n>32 then n:=32;
        pmove(j,n,false);blockwrite(f2,buf,n);if ioresult<>0 then ok:=false;j:=j+n;end;end
      else begin blockwrite(f2,buf,k);if ioresult<>0 then ok:=false;end;
  end;
  if ok and b then begin user(du);rewrite(f2);b:=false;if ioresult<>0 then ok:=false;end;
  if not b then begin user(du);close(f2);if ioresult<>0 then ok:=false;end;
  user(su);close(f1);n:=ioresult;
  {whole only when both hold the same number of records: a disk that filled, or
   a file past what Turbo's 16-bit record count follows, is not a copy}
  if ok then ok:=samesize(s1,su,s2,du);
  if (not ok)and(not b) then delfile(s2,du);
  if ok and(fl>0) then setflags(s2,du,fl);
end;
{$i+}
end;

{---------------------------------------------------------------------------
 The operations, on the marked files of the current panel, or on the
 current file when none is marked.}

function plainname(p,n:integer):boolean;
{a name CP/M's file calls can open: no small letters, no space inside the name
 or the type (programs that write such names exist; FM lists them but cannot
 work on them)}
var g:integer;ok,gap:boolean;
begin
pent(p,n,false);ok:=true;gap:=false;
for g:=2 to 9 do begin
  if (ent[g]>=97)and(ent[g]<=122) then ok:=false;
  if ent[g]=32 then gap:=true else if gap then ok:=false;
end;
gap:=false;
for g:=10 to 12 do begin
  if (ent[g]>=97)and(ent[g]<=122) then ok:=false;
  if ent[g]=32 then gap:=true else if gap then ok:=false;
end;
plainname:=ok;
end;

procedure selection;
{the marked files, or the one under the cursor; a name CP/M cannot open is left
 out, and said}
var n,odd:integer;
begin
nsel:=0;odd:=0;
for n:=1 to cnt[cd] do if mk[cd][idx[cd][n]] then
  if plainname(cd,idx[cd][n]) then begin nsel:=nsel+1;sel[nsel]:=idx[cd][n];end else odd:=odd+1;
if (nsel=0)and(odd=0)and(cnt[cd]>0) then begin
  pent(cd,idx[cd][cur[cd]],false);
  if ent[0]=0 then
    if plainname(cd,idx[cd][cur[cd]]) then begin nsel:=1;sel[1]:=idx[cd][cur[cd]];end else odd:=1;
end;
if odd>0 then begin
  dialog(4,'FM5');str(odd,o);
  dline(1,o+' name(s) CP/M cannot open');dline(2,'are left out. Press a key');
  read(kbd,c);drawall;
end;
end;

procedure reread;
begin
readp(0);readp(1);drawall;
end;

procedure transfer(move:boolean);
var n,g:integer;s1,s2,nm:tip1;r:char;ok,all,stop:boolean;w:tip3;
begin
selection;
if nsel>0 then
if (drv[0]=drv[1])and(usr[0]=usr[1]) then begin
  dialog(4,'FM5');dline(1,'Both panels show the same folder.');dline(2,'Press a key');read(kbd,r);drawall;end
else begin
  if move then w:='Move' else w:='Copy';
  dialog(6,w);
  str(nsel,o);
  if nsel=1 then dline(1,w+' '+copy(fname(cd,sel[1]),3,12)) else dline(1,w+' '+o+' files');
  dline(2,'to '+title(1-cd));
  r:=yesno(4,'Go ahead? (Y/N)');
  all:=false;stop:=r<>'Y';n:=1;
  while (n<=nsel)and not stop do begin
    s1:=fname(cd,sel[n]);nm:=copy(s1,3,12);s2:=chr(65+drv[1-cd])+':'+nm;
    str(n,o);dline(3,o+': '+nm);
    r:='Y';
    if (flagsof(s2,usr[1-cd])>=0)and not all then begin
      r:=yesno(4,nm+' is there. Replace? (Y/N/(A)ll)');
      if r='A' then begin all:=true;r:='Y';end;
      if r=chr(27) then stop:=true;
      dline(4,'');
    end;
    if r='Y' then begin
      cop(s1,usr[cd],s2,usr[1-cd],ok);
      if ok and move then delfile(s1,usr[cd]);
      if not ok then begin dline(4,'Could not copy '+nm+'. Press a key');read(kbd,r);end;
    end;
    n:=n+1;
  end;
  {nothing done (the question answered no or Esc): the marks stay}
  if n>1 then reread else drawall;
end;
end;

procedure erasef;
var n:integer;s1,nm:tip1;r:char;stop:boolean;
begin
selection;
if nsel>0 then begin
  dialog(5,'Erase');str(nsel,o);
  if nsel=1 then dline(1,'Erase '+copy(fname(cd,sel[1]),3,12)) else dline(1,'Erase '+o+' files');
  r:=yesno(3,'Go ahead? (Y/N)');stop:=r<>'Y';n:=1;
  while (n<=nsel)and not stop do begin
    s1:=fname(cd,sel[n]);nm:=copy(s1,3,12);
    r:='Y';
    if (flagsof(s1,usr[cd]) and 1)<>0 then begin
      r:=yesno(3,nm+' is read-only. Erase it? (Y/N)');if r=chr(27) then stop:=true;end;
    if r='Y' then delfile(s1,usr[cd]);
    n:=n+1;
  end;
  if n>1 then reread else drawall;
end;
end;

procedure renamef;
var n,fl:integer;s1,s2,nm:tip1;t:tip3;nb:array[0..15]of byte;
begin
{the file under the cursor, whatever is marked}
nsel:=0;if cnt[cd]>0 then begin pent(cd,idx[cd][cur[cd]],false);
  if (ent[0]=0)and plainname(cd,idx[cd][cur[cd]]) then begin nsel:=1;sel[1]:=idx[cd][cur[cd]];end;end;
if nsel>0 then begin
  s1:=fname(cd,sel[1]);nm:=copy(s1,3,12);
  dialog(4,'Rename '+nm);t:=nm;
  if editline(2,12,'New name: ',t) then begin nm:=t;
  if pos(' ',nm)>0 then nm:=copy(nm,1,pos(' ',nm)-1);
  if (nm<>'')and(nm<>copy(s1,3,12)) then begin
    s2:=chr(65+drv[cd])+':'+nm;
    if not goodname(nm) then begin dline(2,nm+' is not a file name. Press a key');read(kbd,c);end
    else if flagsof(s2,usr[cd])>=0 then begin dline(2,nm+' is there already. Press a key');read(kbd,c);end
    else begin
      fl:=flagsof(s1,usr[cd]);
      if fl>0 then setflags(s1,usr[cd],0);
      {BDOS 23 takes the old name at $5C and the new one at $6C; mkfcb clears
       $5C+12 to $5C+35, so the new name goes in after the old one is built}
      mkfcb(s2);for n:=0 to 15 do nb[n]:=mem[$5c+n];
      mkfcb(s1);for n:=0 to 15 do mem[$6c+n]:=nb[n];user(usr[cd]);bdos(23,$5c);
      if fl>0 then setflags(s2,usr[cd],fl);
    end;
  end;
  end;
  reread;
end;
end;

procedure view;
{a text file, wrapped at the screen's width: arrows, Enter and Space page,
 Esc goes back}
var s1:tip1;ln:string[255];topl,topo,nextl,nexto,l,row,g,shown:integer;r:char;eofv:boolean;
begin
if cnt[cd]>0 then pent(cd,idx[cd][cur[cd]],false) else ent[0]:=1;
if ent[0]=0 then begin
  s1:=fname(cd,idx[cd][cur[cd]]);topl:=0;topo:=0;
  repeat
    user(usr[cd]);assign(ft,s1);
    {$i-}
    reset(ft);
    {$i+}
    if ioresult<>0 then r:=chr(27)
    else begin
      paper(0);ink(7);clrscr;
      paper(5);ink(0);at(1,1);o:=' '+s1+' ';write(o);spaces(sw-1-length(o));
      paper(0);ink(7);
      l:=0;while (l<topl)and not eof(ft) do begin readln(ft,ln);l:=l+1;end;
      {the page: from line topl, topo characters into it; it ends at the line and
       character the next page starts from}
      row:=2;shown:=0;nextl:=topl;nexto:=0;
      while (row<sh)and not eof(ft) do begin
        readln(ft,ln);shown:=shown+1;
        for g:=1 to length(ln) do if (ln[g]<' ')or(ln[g]>'~') then ln[g]:='.';
        if shown=1 then begin ln:=copy(ln,topo+1,255);g:=topo;end else g:=0;
        repeat
          at(1,row);write(copy(ln,1,sw-1));ln:=copy(ln,sw,255);row:=row+1;g:=g+sw-1;
        until (length(ln)=0)or(row>=sh);
        if length(ln)>0 then begin nextl:=topl+shown-1;nexto:=g;end
        else begin nextl:=topl+shown;nexto:=0;end;
      end;
      eofv:=eof(ft)and(nexto=0);close(ft);
      paper(0);ink(6);at(1,sh);
      if sw>=60 then begin
        if eofv then write('End of the file. Up goes back, Esc returns')
        else write('Down, Enter or Space: more. Up: back. Esc returns');
      end
      else if eofv then write('End. Up: back. Esc: out')
      else write('Down: more. Up: back. Esc: out');
      repeat read(kbd,r);
      until (r=chr(27))or(r='Q')or(r='q')or(r=chr(13))or(r=' ')or(r=chr(24))or(r=chr(5))or(r=chr(3))or(r=chr(18));
      if (r=chr(5))or(r=chr(18)) then begin topl:=topl-(sh-2);if topl<0 then topl:=0;topo:=0;end
      else if not eofv then begin topl:=nextl;topo:=nexto;end;
    end;
  until (r=chr(27))or(r='Q')or(r='q');
  drawall;
end;
end;

procedure choosedrive;
var g,u:integer;r:char;done:boolean;
begin
dialog(npd+3,'Drive');
for g:=1 to npd do dline(g,chr(65+pdv[g])+':');
u:=1;for g:=1 to npd do if pdv[g]=drv[cd] then u:=g;
done:=false;
repeat
  paper(5);ink(0);at(dx+2,dy+u);lowvideo;write(chr(65+pdv[u]),':');highvideo;
  read(kbd,r);r:=upcase(r);
  paper(5);ink(0);at(dx+2,dy+u);write(chr(65+pdv[u]),':');
  if (r=chr(24))and(u<npd) then u:=u+1;
  if (r=chr(5))and(u>1) then u:=u-1;
  for g:=1 to npd do if r=chr(65+pdv[g]) then begin u:=g;r:=chr(13);end;
  if (r=chr(13))or(r=chr(27)) then done:=true;
until done;
if r=chr(13) then begin drv[cd]:=pdv[u];usr[cd]:=0;readp(cd);end;
drawall;
end;
