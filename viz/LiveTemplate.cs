internal static class LiveTemplate
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<title>Gi — live field</title>
<style>
  html,body{margin:0;height:100%;background:#0b1020;overflow:hidden;font:13px/1.4 system-ui,sans-serif;color:#cfd8ff;display:flex;flex-direction:column}
  #top{padding:10px 14px 8px;background:#0b1020;border-bottom:1px solid #1d2748;z-index:5}
  #scenes{display:flex;gap:6px;flex-wrap:wrap;justify-content:center}
  .chip{display:inline-flex;gap:6px;align-items:center;cursor:pointer;padding:4px 12px;border:1px solid #2a3555;border-radius:14px;user-select:none;font-size:12px}
  .chip.on{background:#22304f;border-color:#3d4f7d}
  .chip .dot{width:8px;height:8px;border-radius:50%}
  #caption{margin:7px auto 0;max-width:940px;text-align:center;color:#8fa1d6;font-size:12px}
  #wrap{position:relative;flex:1}
  #c{display:block;width:100%;height:100%}
  #hud{position:absolute;top:12px;left:12px;background:#131a30cc;border:1px solid #2a3555;border-radius:8px;padding:10px 14px;min-width:220px;max-height:calc(100% - 30px);overflow:auto;backdrop-filter:blur(4px)}
  #hud h1{font-size:12px;margin:0 0 8px;letter-spacing:.08em;text-transform:uppercase;color:#8fa1d6}
  .row{display:flex;gap:6px;align-items:center;margin:6px 0;flex-wrap:wrap}
  input[type=range]{width:110px}
  #stats{margin-top:8px;font-size:11px;color:#7a89b8;white-space:pre-wrap}
  #fstats{margin-top:4px;font-size:11px;color:#8fa1d6;white-space:pre-wrap}
  #receipts{margin-top:6px;font:10px/1.5 ui-monospace,monospace;color:#9fb0e0;white-space:pre-wrap;border-top:1px solid #22304f;padding-top:6px}
  .plabel{position:absolute;color:#e6ecff;font-size:11px;text-shadow:0 1px 3px #000;pointer-events:none;transform:translate(-50%,-50%);white-space:nowrap}
  #hint{position:absolute;bottom:10px;left:12px;font-size:11px;color:#5a6890}
</style>
</head>
<body>
<div id='top'><div id='scenes'>
  <span class='chip on'>ecosystem · live</span>
  <span class='chip' id='dot'>connecting</span>
</div>
<div id='caption'>The gallery heightfield, ticking: 120 sheep steer on World.QueryGradient while 8 wolves hunt the herd layer's region QueryMax. Right-drag paints fear or food; T rewinds the field 5 seconds via World.Rewind.</div></div>
<div id='wrap'>
  <canvas id='c'></canvas>
  <div id='hud'>
    <h1>Gi field</h1>
    <div id='layers' class='row'></div>
    <div class='row'><span>height</span><input id='hs' type='range' min='0' max='200' value='100'><span id='hsv'>1.0x</span></div>
    <div class='row'><label><input id='wire' type='checkbox'> wire</label><label><input id='mk' type='checkbox' checked> agents</label><label><input id='spin' type='checkbox' checked> spin</label></div>
    <div class='row'><span>right-drag</span><span class='chip on' id='pf'>fear</span><span class='chip' id='pd'>food</span><span class='chip' id='rst'>reset</span></div>
    <div id='stats'></div>
    <div id='fstats'></div>
    <div id='receipts'></div>
  </div>
  <div id='labels'></div>
  <div id='hint'>drag = orbit · wheel = zoom · right-drag = paint · R = reset · T = rewind 5s</div>
</div>
<script>
const cv=document.getElementById('c'), wrap=document.getElementById('wrap');
const gl=cv.getContext('webgl',{antialias:true});
if(!gl) document.body.innerHTML='<p style=\'padding:2em\'>WebGL unavailable</p>';
gl.getExtension('OES_element_index_uint');

const VS=`attribute vec3 pos; attribute vec3 nrm; attribute vec3 col;
uniform mat4 mvp; varying vec3 vn; varying vec3 vc;
void main(){ vn=nrm; vc=col; gl_Position=mvp*vec4(pos,1.0); }`;
const FS=`precision mediump float; varying vec3 vn; varying vec3 vc;
void main(){
  vec3 n=normalize(vn);
  float d=max(dot(n,normalize(vec3(0.45,0.8,0.35))),0.0);
  vec3 c=vc*(0.25+0.75*d);
  c+=pow(d,8.0)*0.15;
  gl_FragColor=vec4(c,1.0);}`;
const PVS=`attribute vec3 pos; attribute vec3 col; uniform mat4 mvp;
varying vec3 vc; void main(){ vc=col; gl_Position=mvp*vec4(pos,1.0); gl_PointSize=7.0; }`;
const PFS=`precision mediump float; varying vec3 vc;
void main(){ vec2 q=gl_PointCoord-0.5; if(dot(q,q)>0.25) discard; gl_FragColor=vec4(vc,1.0); }`;
function sh(t,s){const h=gl.createShader(t);gl.shaderSource(h,s);gl.compileShader(h);
  if(!gl.getShaderParameter(h,gl.COMPILE_STATUS))console.error(gl.getShaderInfoLog(h));return h;}
function prog(vs,fs){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,vs));
  gl.attachShader(p,sh(gl.FRAGMENT_SHADER,fs));gl.linkProgram(p);return p;}
const meshP=prog(VS,FS), ptP=prog(PVS,PFS);

function ramp(v,m){
  const t=Math.max(-1,Math.min(1,v/m));
  const stops=t>=0
    ? [[0.00,[0.10,0.14,0.30]],[0.25,[0.00,0.55,0.85]],[0.55,[0.15,0.90,0.75]],[0.80,[1.00,0.85,0.25]],[1.00,[1.00,0.20,0.25]]]
    : [[0.00,[0.10,0.14,0.30]],[0.50,[0.35,0.10,0.55]],[1.00,[0.85,0.15,0.75]]];
  const u=Math.abs(t);
  for(let i=1;i<stops.length;i++)
    if(u<=stops[i][0]||i===stops.length-1){
      const f=(stops[i][0]-stops[i-1][0])>0?(u-stops[i-1][0])/(stops[i][0]-stops[i-1][0]):0;
      const a=stops[i-1][1],b=stops[i][1];
      return [a[0]+(b[0]-a[0])*f, a[1]+(b[1]-a[1])*f, a[2]+(b[2]-a[2])*f];
    }
}

const N=256, n=N*N;
const idx=new Uint32Array((N-1)*(N-1)*6), lidx=new Uint32Array((N-1)*(N-1)*4);
let q=0,r=0;
for(let y=0;y<N-1;y++)for(let x=0;x<N-1;x++){const i=y*N+x;
  idx[q++]=i;idx[q++]=i+1;idx[q++]=i+N;idx[q++]=i+1;idx[q++]=i+N+1;idx[q++]=i+N;
  lidx[r++]=i;lidx[r++]=i+1;lidx[r++]=i;lidx[r++]=i+N;}
const vals=new Int32Array(n), pos=new Float32Array(n*3), col=new Float32Array(n*3), nrm=new Float32Array(n*3);
const vbo=gl.createBuffer(), cbo=gl.createBuffer(), nbo=gl.createBuffer(),
      ibo=gl.createBuffer(), libo=gl.createBuffer();
gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ibo);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,idx,gl.STATIC_DRAW);
gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,libo);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,lidx,gl.STATIC_DRAW);
let mn=0,mx=0,nz=0,m=1;

const LAYERS=[{name:'threat',color:'#ff5252'},{name:'food',color:'#69f0ae'},{name:'herd',color:'#64dfff'}];
const raw=[new Int16Array(n),new Int16Array(n),new Int16Array(n)];
const active=new Set([0,1,2]);
let HS=1.0, dirty=true, tickN=0, eaten=0;

const MAXA=256;
const pAx=new Float32Array(MAXA), pAy=new Float32Array(MAXA),
      cAx=new Float32Array(MAXA), cAy=new Float32Array(MAXA);
let shN=0, wfN=0, tPrev=0;
const pts=new Float32Array(MAXA*3), cls=new Float32Array(MAXA*3);
const pbo=gl.createBuffer(), cpo=gl.createBuffer();

function panelValues(){
  vals.fill(0);
  for(const li of active){const a=raw[li];for(let i=0;i<n;i++)vals[i]+=a[i];}
  mn=0;mx=0;nz=0;
  for(let i=0;i<n;i++){const v=vals[i];if(v<mn)mn=v;if(v>mx)mx=v;if(v!==0)nz++;}
  m=Math.max(1,mx,-mn);
}
function normals(){
  const h=i=>vals[i]*HS*0.06;
  for(let y=0;y<N;y++)for(let x=0;x<N;x++){
    const i=y*N+x;
    const hl=x>0?h(i-1):h(i),hr=x<N-1?h(i+1):h(i);
    const hd=y>0?h(i-N):h(i),hu=y<N-1?h(i+N):h(i);
    let nx=hl-hr,ny=2,nzc=hd-hu;
    const l=Math.hypot(nx,ny,nzc)||1;
    nrm[i*3]=nx/l;nrm[i*3+1]=ny/l;nrm[i*3+2]=nzc/l;
    pos[i*3+1]=vals[i]*HS*0.06;
  }
}
function geom(){
  for(let y=0;y<N;y++)for(let x=0;x<N;x++){
    const i=y*N+x;
    pos[i*3]=-128+(x+0.5);
    pos[i*3+2]=-128+(y+0.5);
    const c=ramp(vals[i],m);
    col[i*3]=c[0];col[i*3+1]=c[1];col[i*3+2]=c[2];
  }
}
function upload(){
  normals();
  gl.bindBuffer(gl.ARRAY_BUFFER,vbo);gl.bufferData(gl.ARRAY_BUFFER,pos,gl.DYNAMIC_DRAW);
  gl.bindBuffer(gl.ARRAY_BUFFER,nbo);gl.bufferData(gl.ARRAY_BUFFER,nrm,gl.DYNAMIC_DRAW);
  gl.bindBuffer(gl.ARRAY_BUFFER,cbo);gl.bufferData(gl.ARRAY_BUFFER,col,gl.DYNAMIC_DRAW);
}
const fstatsEl=document.getElementById('fstats');
function rebuild(){
  panelValues();geom();upload();dirty=false;
  fstatsEl.textContent='field: min '+mn+' · max '+mx+' · '+nz.toLocaleString()+' covered · eaten '+eaten+' · tick '+tickN;
}

let ws=null;
const dotEl=document.getElementById('dot');
function setConn(ok,label){dotEl.textContent=label;dotEl.className='chip'+(ok?' on':'');}
function connect(){
  setConn(false,'connecting');
  let sock;
  try{sock=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/ws');}
  catch(e){setTimeout(connect,1500);return;}
  sock.binaryType='arraybuffer';
  sock.onopen=()=>{ws=sock;setConn(true,'live');};
  sock.onclose=()=>{if(ws===sock)ws=null;setConn(false,'reconnecting');setTimeout(connect,1500);};
  sock.onerror=()=>{try{sock.close();}catch(e){}};
  sock.onmessage=ev=>{
    if(typeof ev.data==='string'){setStats(ev.data);return;}
    const buf=ev.data, dv=new DataView(buf);
    const t=dv.getInt32(0,true), s=dv.getInt32(4,true), w=dv.getInt32(8,true);
    if(s+w>MAXA||buf.byteLength<16+8*(s+w)+6*n)return;
    eaten=dv.getInt32(12,true);
    tickN=t;
    let off=16;
    for(let i=0;i<s+w;i++){pAx[i]=cAx[i];pAy[i]=cAy[i];}
    for(let i=0;i<s;i++){cAx[i]=dv.getFloat32(off,true);cAy[i]=dv.getFloat32(off+4,true);off+=8;}
    for(let i=0;i<w;i++){cAx[s+i]=dv.getFloat32(off,true);cAy[s+i]=dv.getFloat32(off+4,true);off+=8;}
    raw[0].set(new Int16Array(buf,off,n));off+=2*n;
    raw[1].set(new Int16Array(buf,off,n));off+=2*n;
    raw[2].set(new Int16Array(buf,off,n));
    shN=s;wfN=w;tPrev=performance.now();dirty=true;
  };
}
const statsEl=document.getElementById('stats'), receiptsEl=document.getElementById('receipts');
function setStats(text){
  const i=text.indexOf('\n—\n');
  if(i<0){statsEl.textContent=text;return;}
  statsEl.textContent=text.slice(0,i);
  receiptsEl.textContent=text.slice(i+3);
}
function sendOp(op,x,y){
  if(!ws||ws.readyState!==1)return;
  const b=new ArrayBuffer(9), d=new DataView(b);
  d.setUint8(0,op);d.setFloat32(1,x,true);d.setFloat32(5,y,true);
  ws.send(b);
}

const layersEl=document.getElementById('layers');
function buildLayerChips(){
  layersEl.innerHTML='';
  LAYERS.forEach((L,li)=>{
    const b=document.createElement('span');
    b.className='chip'+(active.has(li)?' on':'');
    b.innerHTML=`<span class='dot' style='background:${L.color}'></span>${L.name}`;
    b.onclick=()=>{if(active.has(li))active.delete(li);else active.add(li);buildLayerChips();dirty=true;};
    layersEl.appendChild(b);
  });
}

const labelsEl=document.getElementById('labels');
const labelEl=document.createElement('div');
labelEl.className='plabel';
labelEl.textContent='256² · 1 unit/cell · live';
labelsEl.appendChild(labelEl);

const cam={yaw:0.7,pitch:0.85,dist:430};
let drag=false,lx=0,ly=0;
cv.addEventListener('mousedown',e=>{if(e.button===0){drag=true;lx=e.clientX;ly=e.clientY;}});
addEventListener('mouseup',()=>drag=false);
addEventListener('mousemove',e=>{if(!drag)return;
  cam.yaw+=(e.clientX-lx)*0.006;
  cam.pitch=Math.min(1.5,Math.max(0.1,cam.pitch+(e.clientY-ly)*0.006));
  lx=e.clientX;ly=e.clientY;});
cv.addEventListener('wheel',e=>{e.preventDefault();
  cam.dist*=e.deltaY>0?1.09:0.92;cam.dist=Math.min(3000,Math.max(60,cam.dist));},{passive:false});

function groundHit(e){
  const rect=cv.getBoundingClientRect();
  const nx=((e.clientX-rect.left)/rect.width)*2-1;
  const ny=1-((e.clientY-rect.top)/rect.height)*2;
  const ex=Math.cos(cam.pitch)*Math.cos(cam.yaw)*cam.dist,
        ey=Math.sin(cam.pitch)*cam.dist,
        ez=Math.cos(cam.pitch)*Math.sin(cam.yaw)*cam.dist;
  let fx=-ex,fy=-ey,fz=-ez;
  const fl=Math.hypot(fx,fy,fz);fx/=fl;fy/=fl;fz/=fl;
  let rx=-fz,ry=0,rz=fx;
  const rl=Math.hypot(rx,ry,rz)||1;rx/=rl;rz/=rl;
  const vx=-rz*fy, vy=rz*fx-rx*fz, vz=rx*fy;
  const tt=Math.tan(0.45), asp=rect.width/rect.height;
  let dx=fx+rx*tt*asp*nx+vx*tt*ny,
      dy=fy+ry*tt*asp*nx+vy*tt*ny,
      dz=fz+rz*tt*asp*nx+vz*tt*ny;
  const dl=Math.hypot(dx,dy,dz);dx/=dl;dy/=dl;dz/=dl;
  if(dy>=-1e-4)return null;
  const k=-ey/dy;
  if(k<0||k>1e4)return null;
  return {x:ex+dx*k+128, y:ez+dz*k+128};
}

let paint=0;
const lastFood={x:-99,y:-99};
const pf=document.getElementById('pf'), pd=document.getElementById('pd');
pf.onclick=()=>{paint=0;pf.classList.add('on');pd.classList.remove('on');};
pd.onclick=()=>{paint=1;pd.classList.add('on');pf.classList.remove('on');};
document.getElementById('rst').onclick=()=>sendOp(3,0,0);
addEventListener('keydown',e=>{
  if(e.key==='r'||e.key==='R')sendOp(3,0,0);
  if(e.key==='t'||e.key==='T')sendOp(4,0,0);});
cv.addEventListener('contextmenu',e=>e.preventDefault());
let rdown=false;
cv.addEventListener('mousedown',e=>{if(e.button===2){rdown=true;paintAt(e);}});
addEventListener('mouseup',e=>{
  if(e.button===2&&rdown){rdown=false;if(paint===0)sendOp(1,0,0);}});
addEventListener('mousemove',e=>{if(rdown)paintAt(e);});
function paintAt(e){
  const g=groundHit(e);
  if(!g||g.x<0||g.x>256||g.y<0||g.y>256)return;
  if(paint===0)sendOp(0,g.x,g.y);
  else{
    const dx=g.x-lastFood.x, dy=g.y-lastFood.y;
    if(dx*dx+dy*dy>=9){sendOp(2,g.x,g.y);lastFood.x=g.x;lastFood.y=g.y;}
  }
}

const hs=document.getElementById('hs'),hsv=document.getElementById('hsv');
hs.oninput=()=>{HS=hs.value/100;hsv.textContent=HS.toFixed(1)+'x';upload();};

function matMul(a,b){const o=new Float32Array(16);
  for(let i=0;i<4;i++)for(let j=0;j<4;j++){let s=0;
    for(let k=0;k<4;k++)s+=a[k*4+j]*b[i*4+k];o[i*4+j]=s;}return o;}
function persp(fov,asp,n,f){const t=1/Math.tan(fov/2),d=1/(n-f);
  return new Float32Array([t/asp,0,0,0, 0,t,0,0, 0,0,(f+n)*d,-1, 0,0,2*f*n*d,0]);}
function look(ex,ey,ez,cx,cy,cz){
  let fx=cx-ex,fy=cy-ey,fz=cz-ez,fl=Math.hypot(fx,fy,fz);fx/=fl;fy/=fl;fz/=fl;
  const ux=0,uy=1,uz=0;
  let rx=fy*uz-fz*uy, ry=fz*ux-fx*uz, rz=fx*uy-fy*ux;
  const rl=Math.hypot(rx,ry,rz)||1;
  const sx=rx/rl,sy=ry/rl,sz=rz/rl;
  const vx=sy*fz-sz*fy, vy=sz*fx-sx*fz, vz=sx*fy-sy*fx;
  return new Float32Array([sx,vx,-fx,0, sy,vy,-fy,0, sz,vz,-fz,0,
    -(sx*ex+sy*ey+sz*ez), -(vx*ex+vy*ey+vz*ez), (fx*ex+fy*ey+fz*ez), 1]);
}

function buildAgents(){
  const f=Math.min(1,(performance.now()-tPrev)/55);
  const total=shN+wfN;
  for(let i=0;i<total;i++){
    pts[i*3]=pAx[i]+(cAx[i]-pAx[i])*f-128;
    pts[i*3+1]=6;
    pts[i*3+2]=pAy[i]+(cAy[i]-pAy[i])*f-128;
    if(i<shN){cls[i*3]=0.89;cls[i*3+1]=0.89;cls[i*3+2]=0.84;}
    else{cls[i*3]=0.72;cls[i*3+1]=0.18;cls[i*3+2]=0.18;}
  }
  if(!total)return;
  gl.bindBuffer(gl.ARRAY_BUFFER,pbo);gl.bufferData(gl.ARRAY_BUFFER,pts.subarray(0,total*3),gl.DYNAMIC_DRAW);
  gl.bindBuffer(gl.ARRAY_BUFFER,cpo);gl.bufferData(gl.ARRAY_BUFFER,cls.subarray(0,total*3),gl.DYNAMIC_DRAW);
}

function draw(){
  const w=cv.width=wrap.clientWidth*devicePixelRatio|0;
  const h=cv.height=wrap.clientHeight*devicePixelRatio|0;
  gl.viewport(0,0,w,h);
  gl.enable(gl.DEPTH_TEST);
  gl.clearColor(0.043,0.063,0.125,1);
  gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
  if(dirty)rebuild();
  if(document.getElementById('spin').checked&&!drag)cam.yaw+=0.003;
  const ex=Math.cos(cam.pitch)*Math.cos(cam.yaw)*cam.dist,
        ey=Math.sin(cam.pitch)*cam.dist,
        ez=Math.cos(cam.pitch)*Math.sin(cam.yaw)*cam.dist;
  const mvp=matMul(persp(0.9,w/h,1,8000),look(ex,ey,ez,0,0,0));
  gl.useProgram(meshP);
  gl.uniformMatrix4fv(gl.getUniformLocation(meshP,'mvp'),false,mvp);
  const pa=gl.getAttribLocation(meshP,'pos'),na=gl.getAttribLocation(meshP,'nrm'),ca=gl.getAttribLocation(meshP,'col');
  const wire=document.getElementById('wire').checked, mk=document.getElementById('mk').checked;
  gl.bindBuffer(gl.ARRAY_BUFFER,vbo);gl.vertexAttribPointer(pa,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pa);
  gl.bindBuffer(gl.ARRAY_BUFFER,nbo);gl.vertexAttribPointer(na,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(na);
  gl.bindBuffer(gl.ARRAY_BUFFER,cbo);gl.vertexAttribPointer(ca,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(ca);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,wire?libo:ibo);
  gl.drawElements(wire?gl.LINES:gl.TRIANGLES,wire?lidx.length:idx.length,gl.UNSIGNED_INT,0);
  const total=shN+wfN;
  if(mk&&total){
    buildAgents();
    gl.useProgram(ptP);
    gl.uniformMatrix4fv(gl.getUniformLocation(ptP,'mvp'),false,mvp);
    const pp=gl.getAttribLocation(ptP,'pos'),pc=gl.getAttribLocation(ptP,'col');
    gl.bindBuffer(gl.ARRAY_BUFFER,pbo);gl.vertexAttribPointer(pp,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pp);
    gl.bindBuffer(gl.ARRAY_BUFFER,cpo);gl.vertexAttribPointer(pc,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pc);
    gl.disable(gl.DEPTH_TEST);
    gl.drawArrays(gl.POINTS,0,shN);
    gl.drawArrays(gl.POINTS,shN,wfN);
    gl.enable(gl.DEPTH_TEST);
  }
  const v=[0,0,280,1];
  const o=[mvp[0]*v[0]+mvp[4]*v[1]+mvp[8]*v[2]+mvp[12]*v[3],
           mvp[1]*v[0]+mvp[5]*v[1]+mvp[9]*v[2]+mvp[13]*v[3],
           mvp[2]*v[0]+mvp[6]*v[1]+mvp[10]*v[2]+mvp[14]*v[3],
           mvp[3]*v[0]+mvp[7]*v[1]+mvp[11]*v[2]+mvp[15]*v[3]];
  if(o[3]<=0)labelEl.style.display='none';
  else{
    labelEl.style.display='';
    labelEl.style.left=((o[0]/o[3]*0.5+0.5)*cv.clientWidth)+'px';
    labelEl.style.top=((1-(o[1]/o[3]*0.5+0.5))*cv.clientHeight)+'px';
  }
  requestAnimationFrame(draw);
}
buildLayerChips();
connect();
draw();
</script>
</body>
</html>";
}
