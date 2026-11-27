{FM5: the file manager for the extended HC2000 (hc2000-fm5), the 1998 FM
 (fm3.pas) and the house FM2 made for the machine as it is now: every drive
 and its named folders, up to 1024 files a panel in paged RAM, the console's
 own size (80 x 30 on the VGA, 64 x 24 on the ROM screen), the PC keyboard.
 Built as FM.COM in C:/bin. Parts: FM5A.PAS, FM5B.PAS and this main program
 (the house Turbo Pascal 1.0).}
{$C-}
{$I FM5A.PAS}
{$I FM5B.PAS}


procedure subline(n:integer;o:tip3);
{record n of a batch: the length, the line, a 0 and a $}
var g:integer;
begin
buf[n*128]:=length(o);o:=o+chr(0)+'$';
for g:=1 to length(o) do buf[n*128+g]:=ord(o[g]);
end;

procedure savestate;
begin
user(hu);assign(ft,hs+'FM5.TMP');rewrite(ft);
writeln(ft,drv[0]);writeln(ft,drv[1]);writeln(ft,usr[0]);writeln(ft,usr[1]);
writeln(ft,cd);writeln(ft,cur[0]);writeln(ft,cur[1]);
close(ft);
end;

procedure loadstate;
var a,b:integer;
begin
user(hu);assign(ft,hs+'FM5.TMP');
{$i-}
reset(ft);
{$i+}
if ioresult=0 then begin
  readln(ft,drv[0]);readln(ft,drv[1]);readln(ft,usr[0]);readln(ft,usr[1]);
  readln(ft,cd);readln(ft,a);readln(ft,b);
  close(ft);erase(ft);
  for i:=0 to 1 do begin
    if not hasdrv(drv[i]) then drv[i]:=hd;
    if (usr[i]<0)or(usr[i]>15) then usr[i]:=hu;
  end;
  if (cd<0)or(cd>1) then cd:=0;
  readp(0);readp(1);
  if a<=cnt[0] then cur[0]:=a;if b<=cnt[1] then cur[1]:=b;
  for i:=0 to 1 do begin
    if cur[i]<1 then cur[i]:=1;
    top[i]:=((cur[i]-1) div nrow-ncol+1)*nrow;if top[i]<0 then top[i]:=0;
  end;
end else begin readp(0);readp(1);end;
end;

procedure leave;
{the console as FM found it, and the batch FM was started from, if any}
begin
paper((attr0 div 8) and 7);ink(attr0 and 7);highvideo;clrscr;showk;
user(hu);
assign(f1,hs+'$$$.SUB');
{$i-}
reset(f1);
{$i+}
if ioresult=0 then begin close(f1);erase(f1);end;
assign(f2,hs+'FM5B.TMP');
{$i-}
reset(f2);
{$i+}
if ioresult=0 then begin close(f2);rename(f2,hs+'$$$.SUB');end;
end;

procedure runcom;
{the program under the cursor, run through a batch in the folder FM was
 started from, which comes back to FM: the batch's last record runs first}
var s:tip1;us:string[4];
begin
s:=copy(fname(cd,idx[cd][cur[cd]]),3,12);s:=copy(s,1,pos('.',s)-1);
cm:='';
dialog(4,'Run '+s);
if editline(2,40,'Parameters: ',cm) then begin
  for i:=0 to 767 do buf[i]:=26;
  subline(0,'FM');
  str(hu,us);subline(1,'USER '+us);
  subline(2,hs);
  if cm='' then subline(3,s) else subline(3,s+' '+cm);
  str(usr[cd],us);subline(4,'USER '+us);
  subline(5,chr(65+drv[cd])+':');
  user(hu);
  assign(f1,hs+'$$$.SUB');rewrite(f1);blockwrite(f1,buf,6);close(f1);
  savestate;
  paper((attr0 div 8) and 7);ink(attr0 and 7);highvideo;clrscr;showk;
  writeln(chr(65+drv[cd]),'>',s,' ',cm);
  bdos(14,hd);mem[4]:=hu*16+hd;halt;
end else drawall;
end;

procedure openent;
{Enter: a folder opens, '..' goes up (onto the folder left), a .COM runs,
 any other file is shown}
var n,u,g:integer;
begin
if cnt[cd]>0 then begin
  n:=idx[cd][cur[cd]];pent(cd,n,false);
  if ent[0]<>0 then begin
    u:=usr[cd];usr[cd]:=ent[1];readp(cd);
    for g:=1 to cnt[cd] do begin pent(cd,idx[cd][g],false);
      if (ent[0]=1)and(ent[1]=u) then cur[cd]:=g;end;
    top[cd]:=((cur[cd]-1) div nrow-ncol+1)*nrow;if top[cd]<0 then top[cd]:=0;
    drawpanel(cd);status;
  end else begin
    o:=fname(cd,n);
    if copy(o,length(o)-3,4)='.COM' then runcom else view;
  end;
end;
end;

procedure markcur;
begin
if cnt[cd]>0 then begin
  pent(cd,idx[cd][cur[cd]],false);
  if ent[0]=0 then mk[cd][idx[cd][cur[cd]]]:=not mk[cd][idx[cd][cur[cd]]];
  drawfoot(cd);
  if cur[cd]<cnt[cd] then moveto(cd,cur[cd]+1) else begin drawent(cd,cur[cd]);status;end;
end;
end;

procedure markall;
var n:integer;b:boolean;
begin
b:=false;for n:=1 to cnt[cd] do if mk[cd][n] then b:=true;
for n:=1 to cnt[cd] do begin pent(cd,n,false);mk[cd][n]:=(ent[0]=0)and not b;end;
drawlist(cd);drawfoot(cd);status;
end;

begin
hd:=bdos(25,0);hu:=bdos(32,255);hs:=chr(65+hd)+':';
drives;
if not pgd then begin
  writeln('FM needs the extended HC2000, with its paged RAM. FM2 runs on every HC2000.');
  halt;
end;
screen;
{the batch FM was started from (FMMAIN's), kept as FM5B.TMP while FM runs}
user(hu);
{FM5B.TMP is FMMAIN's batch only while FM returns from a program it ran (its
 state, FM5.TMP, is there too); one left without it is stale and goes}
assign(f1,hs+'FM5B.TMP');
{$i-}
reset(f1);
{$i+}
outer:=ioresult=0;
if outer then begin
  close(f1);
  assign(ft,hs+'FM5.TMP');
  {$i-}
  reset(ft);
  {$i+}
  if ioresult=0 then close(ft) else begin erase(f1);outer:=false;end;
end;
if outer then begin
  close(f1);
  assign(f2,hs+'$$$.SUB');
  {$i-}
  reset(f2);
  {$i+}
  if ioresult=0 then begin close(f2);erase(f2);end;
end else begin
  assign(f2,hs+'$$$.SUB');
  {$i-}
  reset(f2);
  {$i+}
  {a batch with lines left is the one FM was started from (FMMAIN's); an
   empty one is what the shell leaves after a batch's last line, FM's own
   run-and-return included}
  if ioresult=0 then begin
    close(f2);
    mkfcb(hs+'$$$.SUB');user(hu);bdos(35,$5c);
    if (mem[$5c+33]=0)and(mem[$5c+34]=0)and(mem[$5c+35]=0) then erase(f2)
    else begin rename(f2,hs+'FM5B.TMP');outer:=true;end;
  end;
end;
for i:=0 to 1 do begin drv[i]:=hd;usr[i]:=hu;cnt[i]:=0;top[i]:=0;cur[i]:=1;nfo[i]:=0;end;
cd:=0;
hidek;
loadstate;
drawall;
quit:=false;
repeat
  read(kbd,c);
  if c=chr(5) then moveto(cd,cur[cd]-1);
  if c=chr(24) then moveto(cd,cur[cd]+1);
  if c=chr(19) then moveto(cd,cur[cd]-nrow);
  if c=chr(4) then moveto(cd,cur[cd]+nrow);
  if c=chr(18) then moveto(cd,cur[cd]-ncol*nrow);
  if c=chr(3) then moveto(cd,cur[cd]+ncol*nrow);
  if c=chr(9) then begin cd:=1-cd;drawpanel(0);drawpanel(1);status;end;
  if c=chr(13) then openent;
  if c=' ' then markcur;
  c:=upcase(c);
  if c='C' then transfer(false);
  if c='M' then transfer(true);
  if c='R' then renamef;
  if (c='E')or(c=chr(127)) then erasef;
  if c='V' then view;
  if c='D' then choosedrive;
  if c='A' then markall;
  if c='Q' then quit:=true;
until quit;
leave;
{FM leaves you in the folder its panel shows, unless a batch goes on}
if outer then begin bdos(14,hd);user(hu);mem[4]:=hu*16+hd;end
else begin bdos(14,drv[cd]);user(usr[cd]);mem[4]:=usr[cd]*16+drv[cd];end;
end.
