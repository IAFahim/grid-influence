internal static class Template
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<title>Gi — capability gallery</title>
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
  #receipts{margin-top:6px;font:10px/1.5 ui-monospace,monospace;color:#9fb0e0;white-space:pre-wrap;border-top:1px solid #22304f;padding-top:6px}
  .plabel{position:absolute;color:#e6ecff;font-size:11px;text-shadow:0 1px 3px #000;pointer-events:none;transform:translate(-50%,-50%);white-space:nowrap}
  #hint{position:absolute;bottom:10px;left:12px;font-size:11px;color:#5a6890}
</style>
</head>
<body>
<div id='top'><div id='scenes'></div><div id='caption'></div></div>
<div id='wrap'>
  <canvas id='c'></canvas>
  <div id='hud'>
    <h1>Gi field</h1>
    <div id='layers' class='row'></div>
    <div class='row'><span>height</span><input id='hs' type='range' min='0' max='200' value='100'><span id='hsv'>1.0x</span></div>
    <div class='row'><label><input id='wire' type='checkbox'> wire</label><label><input id='mk' type='checkbox' checked> sources</label><label><input id='spin' type='checkbox' checked> spin</label></div>
    <div id='stats'></div>
    <div id='receipts'></div>
  </div>
  <div id='labels'></div>
  <div id='hint'>drag = orbit · wheel = zoom · ←/→ = scene</div>
</div>
<script>
const DATA=/*__DATA__*/;
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

function hex(h){return [parseInt(h.slice(1,3),16)/255,parseInt(h.slice(3,5),16)/255,parseInt(h.slice(5,7),16)/255];}
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

let si=0, panels=[], active=new Set(), HS=1.0;
const cam={yaw:0.7,pitch:0.85,dist:400};
let drag=false,lx=0,ly=0;

function layout(S){
  const gap=Math.max(...S.panels.map(p=>p.size))*0.16;
  const total=S.panels.reduce((a,p)=>a+p.size,0)+gap*(S.panels.length-1);
  let x=-total/2;
  const oxs=S.panels.map(p=>{const o=x;x+=p.size+gap;return o;});
  return {oxs,total};
}

function makePanel(p,ox){
  const N=p.cells,n=N*N;
  const idx=new Uint32Array((N-1)*(N-1)*6), lidx=new Uint32Array((N-1)*(N-1)*4);
  let q=0,r=0;
  for(let y=0;y<N-1;y++)for(let x=0;x<N-1;x++){const i=y*N+x;
    idx[q++]=i;idx[q++]=i+1;idx[q++]=i+N;idx[q++]=i+1;idx[q++]=i+N+1;idx[q++]=i+N;
    lidx[r++]=i;lidx[r++]=i+1;lidx[r++]=i;lidx[r++]=i+N;}
  return {p,ox,N,step:p.size/N,vals:new Int32Array(n),pos:new Float32Array(n*3),
    col:new Float32Array(n*3),nrm:new Float32Array(n*3),idx,lidx,
    mn:0,mx:0,nz:0,m:1,vbo:null,cbo:null,nbo:null,ibo:null,libo:null,pbo:null,cpo:null,mkN:0,labelEl:null};
}

function panelValues(pl){
  const raw=pl.p.values,n=pl.N*pl.N,vals=pl.vals;
  vals.fill(0);
  for(const li of active){const a=raw[li];for(let i=0;i<n;i++)vals[i]+=a[i];}
  let mn=0,mx=0,nz=0;
  for(let i=0;i<n;i++){const v=vals[i];if(v<mn)mn=v;if(v>mx)mx=v;if(v!==0)nz++;}
  pl.mn=mn;pl.mx=mx;pl.nz=nz;pl.m=Math.max(1,mx,-mn);
}

function normals(pl){
  const N=pl.N,vals=pl.vals,pos=pl.pos,nrm=pl.nrm,h=i=>vals[i]*HS*0.06;
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

function geom(pl){
  const N=pl.N,step=pl.step,ox=pl.ox,pos=pl.pos,col=pl.col;
  for(let y=0;y<N;y++)for(let x=0;x<N;x++){
    const i=y*N+x;
    pos[i*3]=ox+(x+0.5)*step;
    pos[i*3+2]=(y+0.5)*step;
    const c=ramp(pl.vals[i],pl.m);
    col[i*3]=c[0];col[i*3+1]=c[1];col[i*3+2]=c[2];
  }
}

function upload(pl){
  normals(pl);
  const g=gl;
  if(!pl.vbo){pl.vbo=g.createBuffer();pl.cbo=g.createBuffer();pl.nbo=g.createBuffer();
    pl.ibo=g.createBuffer();pl.libo=g.createBuffer();
    g.bindBuffer(g.ELEMENT_ARRAY_BUFFER,pl.ibo);g.bufferData(g.ELEMENT_ARRAY_BUFFER,pl.idx,g.STATIC_DRAW);
    g.bindBuffer(g.ELEMENT_ARRAY_BUFFER,pl.libo);g.bufferData(g.ELEMENT_ARRAY_BUFFER,pl.lidx,g.STATIC_DRAW);}
  g.bindBuffer(g.ARRAY_BUFFER,pl.vbo);g.bufferData(g.ARRAY_BUFFER,pl.pos,g.DYNAMIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,pl.nbo);g.bufferData(g.ARRAY_BUFFER,pl.nrm,g.DYNAMIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,pl.cbo);g.bufferData(g.ARRAY_BUFFER,pl.col,g.DYNAMIC_DRAW);
}

function markers(pl){
  const S=DATA.scenes[si],pts=[],cls=[];
  for(const s of S.sources){
    if(!active.has(s.layer))continue;
    pts.push(pl.ox+s.x,6,s.y);
    const c=hex(S.layers[s.layer].color);
    cls.push(c[0],c[1],c[2]);
  }
  const g=gl;
  if(!pl.pbo){pl.pbo=g.createBuffer();pl.cpo=g.createBuffer();}
  g.bindBuffer(g.ARRAY_BUFFER,pl.pbo);g.bufferData(g.ARRAY_BUFFER,new Float32Array(pts),g.DYNAMIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,pl.cpo);g.bufferData(g.ARRAY_BUFFER,new Float32Array(cls),g.DYNAMIC_DRAW);
  pl.mkN=pts.length/3;
}

function refresh(){
  for(const pl of panels){panelValues(pl);geom(pl);upload(pl);markers(pl);}
  stats();
}

const scenesEl=document.getElementById('scenes');
function buildSceneChips(){
  scenesEl.innerHTML='';
  DATA.scenes.forEach((S,i)=>{
    const b=document.createElement('span');
    b.className='chip'+(i===si?' on':'');
    b.textContent=(i+1)+'. '+S.name;
    b.onclick=()=>loadScene(i);
    scenesEl.appendChild(b);
  });
}

const layersEl=document.getElementById('layers');
function buildLayerChips(){
  const S=DATA.scenes[si];
  layersEl.innerHTML='';
  S.layers.forEach((L,li)=>{
    const b=document.createElement('span');
    b.className='chip'+(active.has(li)?' on':'');
    b.innerHTML=`<span class='dot' style='background:${L.color}'></span>${L.name}`;
    b.onclick=()=>{if(active.has(li))active.delete(li);else active.add(li);buildLayerChips();refresh();};
    layersEl.appendChild(b);
  });
}

const labelsEl=document.getElementById('labels');
function buildLabels(){
  labelsEl.innerHTML='';
  panels.forEach(pl=>{
    const d=document.createElement('div');
    d.className='plabel';d.textContent=pl.p.label;
    labelsEl.appendChild(d);pl.labelEl=d;
  });
}

function stats(){
  const S=DATA.scenes[si];
  const s=panels.map(pl=>`${pl.p.label}: min ${pl.mn} · max ${pl.mx} · ${pl.nz.toLocaleString()} covered`).join('\n');
  document.getElementById('stats').textContent=s;
  document.getElementById('receipts').textContent=S.receipts.join('\n');
}

function loadScene(i){
  si=i;
  for(const pl of panels)
    [pl.vbo,pl.cbo,pl.nbo,pl.ibo,pl.libo,pl.pbo,pl.cpo].forEach(b=>{if(b)gl.deleteBuffer(b);});
  const S=DATA.scenes[i];
  active=new Set(S.layers.map((_,li)=>li));
  const L=layout(S);
  panels=S.panels.map((p,pi)=>makePanel(p,L.oxs[pi]));
  document.getElementById('caption').textContent=S.caption;
  buildSceneChips();buildLayerChips();buildLabels();
  refresh();
  cam.dist=Math.min(1700,Math.max(160,L.total*1.25));
  cam.yaw=0.7;cam.pitch=0.72;
}

const hs=document.getElementById('hs'),hsv=document.getElementById('hsv');
hs.oninput=()=>{HS=hs.value/100;hsv.textContent=HS.toFixed(1)+'x';
  for(const pl of panels)upload(pl);};

addEventListener('keydown',e=>{
  if(e.key==='ArrowRight')loadScene((si+1)%DATA.scenes.length);
  if(e.key==='ArrowLeft')loadScene((si+DATA.scenes.length-1)%DATA.scenes.length);
});
cv.addEventListener('mousedown',e=>{drag=true;lx=e.clientX;ly=e.clientY;});
addEventListener('mouseup',()=>drag=false);
addEventListener('mousemove',e=>{if(!drag)return;
  cam.yaw+=(e.clientX-lx)*0.006;
  cam.pitch=Math.min(1.5,Math.max(0.1,cam.pitch+(e.clientY-ly)*0.006));
  lx=e.clientX;ly=e.clientY;});
cv.addEventListener('wheel',e=>{e.preventDefault();
  cam.dist*=e.deltaY>0?1.09:0.92;cam.dist=Math.min(3000,Math.max(60,cam.dist));},{passive:false});

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

function draw(){
  const w=cv.width=wrap.clientWidth*devicePixelRatio|0;
  const h=cv.height=wrap.clientHeight*devicePixelRatio|0;
  gl.viewport(0,0,w,h);
  gl.enable(gl.DEPTH_TEST);
  gl.clearColor(0.043,0.063,0.125,1);
  gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
  if(document.getElementById('spin').checked&&!drag)cam.yaw+=0.003;
  const ex=Math.cos(cam.pitch)*Math.cos(cam.yaw)*cam.dist,
        ey=Math.sin(cam.pitch)*cam.dist,
        ez=Math.cos(cam.pitch)*Math.sin(cam.yaw)*cam.dist;
  const mvp=matMul(persp(0.9,w/h,1,8000),look(ex,ey,ez,0,0,0));
  gl.useProgram(meshP);
  gl.uniformMatrix4fv(gl.getUniformLocation(meshP,'mvp'),false,mvp);
  const pa=gl.getAttribLocation(meshP,'pos'),na=gl.getAttribLocation(meshP,'nrm'),ca=gl.getAttribLocation(meshP,'col');
  const wire=document.getElementById('wire').checked, mk=document.getElementById('mk').checked;
  for(const pl of panels){
    gl.bindBuffer(gl.ARRAY_BUFFER,pl.vbo);gl.vertexAttribPointer(pa,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pa);
    gl.bindBuffer(gl.ARRAY_BUFFER,pl.nbo);gl.vertexAttribPointer(na,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(na);
    gl.bindBuffer(gl.ARRAY_BUFFER,pl.cbo);gl.vertexAttribPointer(ca,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(ca);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,wire?pl.libo:pl.ibo);
    gl.drawElements(wire?gl.LINES:gl.TRIANGLES,wire?pl.lidx.length:pl.idx.length,gl.UNSIGNED_INT,0);
  }
  if(mk){
    gl.useProgram(ptP);
    gl.uniformMatrix4fv(gl.getUniformLocation(ptP,'mvp'),false,mvp);
    const pp=gl.getAttribLocation(ptP,'pos'),pc=gl.getAttribLocation(ptP,'col');
    for(const pl of panels){
      if(!pl.mkN)continue;
      gl.bindBuffer(gl.ARRAY_BUFFER,pl.pbo);gl.vertexAttribPointer(pp,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pp);
      gl.bindBuffer(gl.ARRAY_BUFFER,pl.cpo);gl.vertexAttribPointer(pc,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pc);
      gl.drawArrays(gl.POINTS,0,pl.mkN);
    }
  }
  for(const pl of panels){
    if(!pl.labelEl)continue;
    const v=[pl.ox+pl.p.size/2,0,pl.p.size*1.04,1];
    const o=[mvp[0]*v[0]+mvp[4]*v[1]+mvp[8]*v[2]+mvp[12]*v[3],
             mvp[1]*v[0]+mvp[5]*v[1]+mvp[9]*v[2]+mvp[13]*v[3],
             mvp[2]*v[0]+mvp[6]*v[1]+mvp[10]*v[2]+mvp[14]*v[3],
             mvp[3]*v[0]+mvp[7]*v[1]+mvp[11]*v[2]+mvp[15]*v[3]];
    if(o[3]<=0){pl.labelEl.style.display='none';continue;}
    pl.labelEl.style.display='';
    pl.labelEl.style.left=((o[0]/o[3]*0.5+0.5)*cv.clientWidth)+'px';
    pl.labelEl.style.top=((1-(o[1]/o[3]*0.5+0.5))*cv.clientHeight)+'px';
  }
  requestAnimationFrame(draw);
}
loadScene(0);
draw();
</script>
</body>
</html>";
}
