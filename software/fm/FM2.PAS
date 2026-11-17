{FM2: the 1998 file manager (fm3.pas), for every drive of the machine.
 Parts: FM2A.PAS, FM2B.PAS and this main program (Turbo Pascal 1.0).}
{$I FM2A.PAS}
{$I FM2B.PAS}
begin
cd:=0;
hd:=bdos(25,0);hu:=bdos(32,255);hs:=chr(65+hd)+':';
drives;

user(hu);
assign(f1,hs+'FM002.TMP');
{$i-}
reset(f1);
{$i+}
if ioresult=0 then begin
              assign(f2,hs+'$$$.SUB');
              {$i-}
              reset(f2);
              {$i+}
              if ioresult=0 then begin close(f2);erase(f2);end;
              end
else begin
assign(f2,hs+'$$$.SUB');
{$i-}
reset(f2);
{$i+}
if ioresult=0 then begin close(f2);rename(f2,hs+'FM002.TMP');end;
end;


for i:=0 to 2 do begin drv[i]:=hd;x[i]:=0;r[i]:=0;
v[i]:=hu;nfd[i]:=0;cv[i]:=0;end;y:=0;
paper(0);border(0);iobord(0);clrscr;
c:=chr(0);for i:=1 to 128 do
          for k:=0 to 1 do begin
          da[k][i]:='';
          sl[k][i]:=chr(0);
          end;
init;
assign(ft,hs+'FM001.TMP');
{$i-}
reset(ft);
{$i+}
if ioresult=0 then begin
readln(ft,drv[0]);readln(ft,drv[1]);
readln(ft,nmf);
if not eof(ft) then begin readln(ft,v[0]);readln(ft,v[1]);end;
close(ft);
for i:=0 to 1 do if not hasdrv(drv[i]) then drv[i]:=hd;
end;
readf(0);
hidek;clikoff;
readf(1);
list(cv[0],0);
list(cv[1],1);
a:=cv[cd];
m:=da[cd];
nf:=nfd[cd];
ink(5);
repeat
c:=chr(0);
desen(cd);
read(kbd,c);

if c=chr(9) then begin cd:=1-cd;list1(cv[0],0);list1(cv[1],1);
end;
case c of
'!':begin select(0);list(cv[0],0);end;
'@':begin select(1);list(cv[1],1);end;
'#':if nfd[cd]>0 then begin clikon;showk;view;end;
'$':begin end;
'%':if nfd[cd]>0 then begin clikon;showk;copyf;end;
'&':if nfd[cd]>0 then begin clikon;showk;movef;end;
'''':begin selusr(0);list(cv[0],0);end;
'(':if nfd[cd]>0 then begin clikon;showk;erasef;end;
')':begin selusr(1);list(cv[1],1);end;
end;
hidek;clikoff;
m:=da[cd];
nf:=nfd[cd];

if c=chr(6) then
if sl[cd][a+y*(nmf+1)+1+x[cd] mod (nmf+1)]=chr(1)
then sl[cd][a+y*(nmf+1)+1+x[cd] mod (nmf+1)]:=chr(0)
else sl[cd][a+y*(nmf+1)+1+x[cd] mod (nmf+1)]:=chr(1);
da[cd]:=m;
a:=cv[cd];

desen(cd);
if nf>0 then begin
y:=x[cd] div (nmf+1);
o:='';

a:=cv[cd];
if c=chr(13) then begin
 for i:=10 to 12 do
 o:=o+chi(m[a+y*(nmf+1)+1+x[cd] mod (nmf+1)][i]);
  if o='COM' then begin {apel procedura de incarcare COM }
                  write(#07);runf;end;
  if o='DEM' then playdem;
  if o='CHN' then begin
  clrscr;
  m[a+y*(nmf+1)+1+x[cd] mod (nmf+1)][9]:='.';
  writeln(chr(65+drv[cd])+'>'+m[a+y*(nmf+1)+1+x[cd] mod (nmf+1)][9]);
  assign(f1,space(m[a+y*(nmf+1)+1+x[cd] mod (nmf+1)]));
  reset(f1);execute(f1);end;

end;
ink(5);
end;

until c='_';
clrscr;showk;clikoff;
paper(1);ink(5);bdos(13);bdos(14,hd);user(hu);

assign(f1,hs+'$$$.SUB');
{$i-}
reset(f1);
{$i+}
if ioresult=0 then begin close(f1);erase(f1);end;

assign(f2,hs+'FM002.TMP');
{$i-}
reset(f2);
{$i+}
if ioresult=0 then begin close(f2);rename(f2,hs+'$$$.SUB');end;

assign(ft,hs+'FM001.TMP');rewrite(ft);writeln(ft,drv[0]);
writeln(ft,drv[1]);writeln(ft,nmf);writeln(ft,v[0]);writeln(ft,v[1]);
close(ft);user(hu);bdos(14,hd);mem[4]:=hu*16+hd;
end.
