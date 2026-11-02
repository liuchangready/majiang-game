// Produces design documents only. Does not modify, crop or resample the PNG artwork.
// Rectangles in this document use a TOP-LEFT origin; runtime libGDX y = 720 - y - h.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const root = path.resolve(__dirname, '../..');
const dir = path.join(root, 'assets/ui/table');
const docs = path.join(root, 'doc/ui/round-preparation');
const pngSize = file => { const b = fs.readFileSync(path.join(dir, file)); return [b.readUInt32BE(16), b.readUInt32BE(20)]; };
const spec = {
  version: 1, designSize: [1280,720], origin: 'top-left', runtimeY: '720 - y - height',
  texture: {filter:'Linear', wrap:'ClampToEdge', mipmaps:false, alpha:'straight; SRC_ALPHA, ONE_MINUS_SRC_ALPHA'},
  files: {
    environment: {file:'club-environment.png', draw:'cover; centered; no pan'},
    board: {file:'jade-table-board.png', draw:'fixed destination; do not resize for drawer'},
    panel: {file:'jade-panel-skin.png', split:[340,340,340,340], destinationBorders:[44,44,44,44]},
    buttons: {file:'button-skins.png', regions:{primary:[40,96,1456,384],secondary:[40,544,1456,384]}, split:[248,248,120,120],destinationBorders:[32,32,18,18]},
    portraits: {file:'animal-medallions.png',regions:{octopus:[0,0,627,627],fox:[627,0,627,627],panda:[0,627,627,627],magpie:[627,627,627,627]}, absoluteSeatMap:['octopus','magpie','panda','fox']},
    targets: {file:'target-card-scenes.png',regions:{CHINITSU_MAN:[16,8,744,324],CHINITSU_SOU:[776,8,744,324],CHINITSU_PIN:[16,340,744,324],DAISANGEN:[776,340,744,324],KOKUSHI:[16,672,744,324],DAISUUSHII:[776,672,744,324]}},
    compass: {file:'round-compass.png',windCenters:[[0.50,0.175],[0.86,0.48],[0.50,0.805],[0.14,0.48]]}
  },
  layers: [
    {z:0,name:'environment',rect:[0,0,1280,720],shared:true},
    {z:10,name:'board',rect:[32,76,1216,620],shared:true},
    {z:20,name:'compass',prep:[344,250,220,220],playing:[546,261,188,188]},
    {z:30,name:'public seats',description:'actual public names, scores, wind and clocks; artwork is decoration'},
    {z:40,name:'real tiles',description:'dealt snapshots only; no fake hands before deal'},
    {z:50,name:'preparation dock / drawer / receipt',description:'local panel translation only'},
    {z:60,name:'selection and focus',description:'gold outline + check, never color alone'},
    {z:70,name:'text, cost, countdown, errors',description:'render dynamically, do not bake into art'},
    {z:100,name:'existing system dialogs',description:'preserve modal priority'}
  ],
  preparation: {
    panel:[936,48,312,624],countdown:[960,124,264,40],openTarget:[956,188,272,156],seeThrough:[956,360,272,64],summary:[956,464,272,66],primary:[956,552,272,76],balance:[956,638,272,20],
    compass:[344,250,220,220],hint:[228,622,552,54],
    seats:{self:{portrait:[16,490,144,144],plate:[20,622,140,50]},left:{portrait:[16,184,144,144],plate:[20,316,140,50]},top:{portrait:[368,8,120,120],plate:[488,30,160,54]},right:{portrait:[800,178,128,128],plate:[804,300,124,50]}},
    drawerRightSeat:{portrait:[536,172,124,124],plate:[538,292,124,50]}
  },
  drawer:{panel:[688,40,560,640],back:[1140,60,88,40],hint:[708,112,520,24],cards:[[708,154,252,114],[976,154,252,114],[708,284,252,114],[976,284,252,114],[708,414,252,114],[976,414,252,114]],seeThrough:[708,544,252,58],cost:[976,544,252,58],skip:[708,616,252,48],confirm:[976,616,252,48]},
  receipt:{panel:[944,160,304,260]},
  playing:{compass:[546,261,188,188],seats:{self:{portrait:[16,564,112,112],plate:[128,614,80,48]},left:{portrait:[16,228,112,112],plate:[16,340,112,58]},top:{portrait:[308,4,112,112],plate:[164,22,148,54]},right:{portrait:[1148,228,112,112],plate:[1148,340,112,58]}},handTray:[208,596,852,110],topTray:[436,8,408,54],actionsX:1078},
  type:{title:34,drawerTitle:28,cardLabel:21,primary:25,body:19,small:16,score:21,ink:'#f8e6aa',muted:'#d3c9a8',gold:'#e7b953',jade:'#95dea6',danger:'#ff9b83'},
  motion:{dock:{fromX:72,durationMs:220},drawer:{fromX:160,durationMs:280,easing:'easeOutCubic'},selection:{liftY:4,durationMs:120},receipt:{fadeMs:150,blocksDealing:false},background:{translation:0,scaleChange:0}}
};
for (const f of Object.values(spec.files)) {
  f.size=pngSize(f.file);
  f.sha256=crypto.createHash('sha256').update(fs.readFileSync(path.join(dir,f.file))).digest('hex');
  for(const r of Object.values(f.regions || {})) {
    if(r[0]<0 || r[1]<0 || r[2]<=0 || r[3]<=0 || r[0]+r[2]>f.size[0] || r[1]+r[3]>f.size[1]) throw Error('Invalid atlas region: '+f.file);
  }
}
spec.estimatedTextureBytes=Object.values(spec.files).reduce((n,f)=>n+f.size[0]*f.size[1]*4,0);
fs.writeFileSync(path.join(dir,'manifest.json'),JSON.stringify(spec,null,2)+'\n');
const esc=s=>String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;');
let a=[];
function text(s,x,y,size=19,color='#f8e6aa',anchor='start',weight=400){a.push(`<text x="${x}" y="${y}" font-size="${size}" fill="${color}" text-anchor="${anchor}" font-weight="${weight}">${esc(s)}</text>`);}
function rect(r,fill,stroke='',sw=1,rx=0){a.push(`<rect x="${r[0]}" y="${r[1]}" width="${r[2]}" height="${r[3]}" fill="${fill}" stroke="${stroke||'none'}" stroke-width="${sw}" rx="${rx}"/>`);}
function region(id,src,dst){a.push(`<svg x="${dst[0]}" y="${dst[1]}" width="${dst[2]}" height="${dst[3]}" viewBox="${src.join(' ')}" preserveAspectRatio="none" overflow="hidden"><use href="#${id}"/></svg>`);}
function image(id,r){const s=spec.files[id].size;region(id,[0,0,...s],r);}
function nine(id,source,dst,splits,borders){
  const [sx,sy,sw,sh]=source,[x,y,w,h]=dst,[l,r,t,b]=splits,[dl,dr,dt,db]=borders;
  const xx=[sx,sx+l,sx+sw-r,sx+sw], yy=[sy,sy+t,sy+sh-b,sy+sh];
  const dx=[x,x+dl,x+w-dr,x+w],dy=[y,y+dt,y+h-db,y+h];
  for(let row=0;row<3;row++)for(let col=0;col<3;col++)region(id,[xx[col],yy[row],xx[col+1]-xx[col],yy[row+1]-yy[row]],[dx[col],dy[row],dx[col+1]-dx[col],dy[row+1]-dy[row]]);
}
function panel(r,b=44){nine('panel',[0,0,...spec.files.panel.size],r,spec.files.panel.split,[b,b,b,b]);}
function button(r,label,primary=true,size=25){nine('buttons',spec.files.buttons.regions[primary?'primary':'secondary'],r,spec.files.buttons.split,spec.files.buttons.destinationBorders);text(label,r[0]+r[2]/2,r[1]+r[3]/2+size*.35,size,'#fff1bb','middle',600);}
function plate(r,name,score,clock){panel(r,12);text(name,r[0]+r[2]/2,r[1]+21,17,'#ffe9a7','middle');text(score,r[0]+r[2]/2,r[1]+42,20,'#fff3d1','middle');if(clock)text(clock,r[0]+r[2]/2,r[1]+55,12,'#98d5b2','middle');}
function seat(kind,r,name,score){region('portraits',spec.files.portraits.regions[kind],r.portrait);plate(r.plate,name,score);}
function eye(x,y){a.push(`<path d="M ${x-16} ${y} Q ${x} ${y-19} ${x+16} ${y} Q ${x} ${y+19} ${x-16} ${y} Z" stroke="#e7b953" stroke-width="3" fill="none"/><circle cx="${x}" cy="${y}" r="5" fill="#e7b953"/>`);}
function check(r,on){rect(r,'#0b211c','#dfb65e',1.7,3);if(on)text('✓',r[0]+r[2]/2,r[1]+r[3]-4,24,'#ffe8a3','middle',700);}
function tile(file,x,y,w,h,angle=0){a.push(`<g transform="rotate(${angle} ${x+w/2} ${y+h/2})"><image href="../../../assets/skins/wikipedia/${file}.png" x="${x}" y="${y}" width="${w}" height="${h}" preserveAspectRatio="xMidYMid meet"/></g>`);}
function compass(r){image('compass',r);text('东一局',r[0]+r[2]/2,r[1]+r[3]*.48,25,'#ffe6a0','middle',600);text('准备开局',r[0]+r[2]/2,r[1]+r[3]*.59,18,'#ffe6a0','middle');for(const [s,c]of [['西',[.5,.175]],['南',[.86,.48]],['东',[.5,.805]],['北',[.14,.48]]])text(s,r[0]+r[2]*c[0],r[1]+r[3]*c[1]+7,21,'#ffe6a0','middle',600);}
function base(mode){
  a=[];a.push(`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1280" height="720" viewBox="0 0 1280 720"><title>正式 APP 素材拼接设计：${mode}（设计稿，非运行截图）</title><defs>`);
  for(const [id,f] of Object.entries(spec.files))a.push(`<image id="${id}" href="../../../assets/ui/table/${f.file}" width="${f.size[0]}" height="${f.size[1]}"/>`);
  a.push(`</defs><g font-family="Noto Sans SC,Microsoft YaHei,sans-serif">`);
  image('environment',[0,0,1280,720]);image('board',spec.layers[1].rect);
  if(mode==='playing')return;
  compass(spec.preparation.compass);
  const s=spec.preparation.seats;
  seat('octopus',s.self,'玩家0 · 东家',25000);seat('fox',s.left,'玩家3 · 北家',25000);seat('panda',s.top,'玩家2 · 西家',25000);seat('magpie',mode==='drawer'?spec.preparation.drawerRightSeat:s.right,'玩家1 · 南家',25000);
  panel(spec.preparation.hint,18);text('◆  准备完成后发牌  ◆',504,657,24,'#ffe7a4','middle');
}
function finish(name){a.push('</g></svg>');fs.writeFileSync(path.join(docs,name),a.join('\n'));}
base('entry');
panel(spec.preparation.panel);text('本局准备',1092,105,34,'#ffe6a0','middle',600);
panel(spec.preparation.countdown,16);text('剩余时间   10 秒',1092,151,21,'#ffe6a0','middle');
panel(spec.preparation.openTarget,22);tile('pin3',976,222,62,82,-12);text('偷天换日',1131,229,27,'#ffe6a0','middle',600);text('◉ 30 点',1130,274,23,'#f4ce73','middle');text('选择后再挑牌型',1092,325,17,'#d3c9a8','middle');
panel(spec.preparation.seeThrough,18);eye(984,392);text('看破 · 10点',1078,399,21,'#fff0b9','middle');check([1188,379,25,25],false);
rect([960,447,264,1],'#c6953f');panel(spec.preparation.summary,17);text('当前方案：不使用千术',1092,492,19,'#a4e5ac','middle');text('预计消耗 0点',1092,517,17,'#edcf89','middle');button(spec.preparation.primary,'开始本局 · 0点');text('剩余出千点 100点',1092,653,16,'#d3c9a8','middle');
finish('ASSEMBLY-01-entry.svg');
base('drawer');panel(spec.drawer.panel);text('偷天换日 · 选择目标',712,86,28,'#ffe6a0','start',600);button(spec.drawer.back,'‹ 返回',false,19);text('影响起手及后续摸牌，不保证成型',968,129,18,'#d3c9a8','middle');
const labels=['清一色（万）','清一色（条）','清一色（筒）','大三元','国士无双','大四喜'];
const faces=[['man1','man2','man3','man4','man5'],['sou1','sou2','sou3','sou4','sou5'],['pin1','pin2','pin3','pin4','pin5'],['dragon1','dragon2','dragon3'],['man1','man9','sou1','sou9','wind1'],['wind1','wind2','wind3','wind4']];
Object.entries(spec.files.targets.regions).forEach(([key,src],i)=>{const r=spec.drawer.cards[i];region('targets',src,r);const fan=faces[i],start=r[0]+r[2]/2-fan.length*21/2;fan.forEach((f,j)=>tile(f,start+j*21,r[1]+25-Math.abs(j-(fan.length-1)/2)*2,30,45,(j-(fan.length-1)/2)*7));text(labels[i],r[0]+126,r[1]+99,21,'#fff0ba','middle',500);if(i===0){rect([r[0]+4,r[1]+3,r[2]-8,r[3]-6],'none','#ffd66e',3,10);check([r[0]+219,r[1]+10,23,23],true);}});
panel([708,544,520,58],18);check([725,560,23,23],false);eye(775,572);text('看破 · 10点',807,579,20);text('本次消耗 30点',1100,579,23,'#f9d986','middle');
button(spec.drawer.skip,'直接开局 · 0点',false,23);button(spec.drawer.confirm,'确认施术 · 30点',true,23);finish('ASSEMBLY-02-targets.svg');
base('receipt');panel(spec.receipt.panel);text('方案已确认',1096,224,31,'#ffe6a0','middle',600);rect([972,247,248,1],'#c6953f');text('不使用千术 · 0点',1096,292,24,'#a4e5ac','middle');text('等待其他玩家完成准备',1096,345,19,'#e7dbb5','middle');text('准备完成后发牌',1096,384,17,'#c2ba9e','middle');finish('ASSEMBLY-03-receipt.svg');
base('playing');
const ps=spec.playing.seats;seat('octopus',ps.self,'玩家0',25000);seat('fox',ps.left,'玩家3 · 北家',25000);seat('panda',ps.top,'玩家2 · 西家',25000);seat('magpie',ps.right,'玩家1 · 南家',25000);
image('compass',spec.playing.compass);text('东一局',640,342,23,'#ffe6a0','middle',600);text('本场0 · 供托0',640,365,15,'#ffe6a0','middle');text('牌山剩余 69',640,387,16,'#d3c9a8','middle');tile('sou3',628,394,24,32);
panel(spec.playing.handTray,20);panel(spec.playing.topTray,12);
const hand=['man1','man2','man3','pin3','pin4','pin5','sou2','sou3','sou4','dragon1','wind1','wind1','wind2','wind3'];hand.forEach((f,i)=>tile(f,220+i*58,610,52,64));
for(let i=0;i<13;i++){rect([446+i*30,18,26,36],'#0d5844','#e0bd68',1,3);rect([246,203+i*26,31,22],'#0d5844','#e0bd68',1,3);rect([990,203+i*26,31,22],'#0d5844','#e0bd68',1,3);}
button([1098,18,154,42],'牌河提示',false,20);['落汗 · 10','偷梁换柱 · 10','袖里藏花 · 10','理牌：关','托管'].forEach((s,i)=>button([1078,469+i*48,150,43],s,i===0,18));
finish('ASSEMBLY-04-playing.svg');
console.log('Wrote manifest and 4 SVG assembly design sheets. No PNG pixels modified.');
