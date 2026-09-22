internal static class Template
{
    public const string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>GridInfluence — field viz</title>
<style>
  html,body{margin:0;height:100%;background:#0b1020;overflow:hidden;font:13px/1.4 system-ui,sans-serif;color:#cfd8ff}
  #c{display:block;width:100%;height:100%}
  #hud{position:fixed;top:12px;left:12px;background:#131a30cc;border:1px solid #2a3555;border-radius:8px;padding:10px 14px;min-width:200px;backdrop-filter:blur(4px)}
  #hud h1{font-size:13px;margin:0 0 8px;letter-spacing:.08em;text-transform:uppercase;color:#8fa1d6}
  .row{display:flex;gap:6px;align-items:center;margin:6px 0}
  .layer{display:inline-flex;gap:5px;align-items:center;cursor:pointer;padding:3px 9px;border:1px solid #2a3555;border-radius:12px;user-select:none}
  .layer.on{background:#22304f}
  .dot{width:8px;height:8px;border-radius:50%}
  input[type=range]{width:110px}
  #stats{margin-top:8px;font-size:11px;color:#7a89b8;white-space:pre}
  #hint{position:fixed;bottom:10px;left:12px;font-size:11px;color:#5a6890}
</style>
</head>
<body>
<canvas id=""c""></canvas>
<div id=""hud"">
  <h1>GridInfluence field</h1>
  <div id=""layers"" class=""row""></div>
  <div class=""row""><span>height</span><input id=""hs"" type=""range"" min=""0"" max=""200"" value=""100""><span id=""hsv"">1.0x</span></div>
  <div class=""row""><label><input id=""wire"" type=""checkbox""> wireframe</label><label><input id=""mk"" type=""checkbox"" checked> sources</label></div>
  <div class=""row""><label><input id=""spin"" type=""checkbox"" checked> auto-rotate</label></div>
  <div id=""stats""></div>
</div>
<div id=""hint"">drag = orbit &middot; wheel = zoom</div>
<script>
const DATA = /*__DATA__*/;

const canvas = document.getElementById('c');
const gl = canvas.getContext('webgl', {antialias:true});
if (!gl) document.body.innerHTML = '<p style=""padding:2em"">WebGL unavailable</p>';
gl.getExtension('OES_element_index_uint');

const VS = `attribute vec3 pos; attribute vec3 nrm; attribute vec3 col;
uniform mat4 mvp; varying vec3 vn; varying vec3 vc;
void main(){ vn=nrm; vc=col; gl_Position=mvp*vec4(pos,1.0); }`;
const FS = `precision mediump float; varying vec3 vn; varying vec3 vc;
void main(){
  vec3 n=normalize(vn);
  float d=max(dot(n,normalize(vec3(0.45,0.8,0.35))),0.0);
  vec3 c=vc*(0.25+0.75*d);
  c+=pow(d,8.0)*0.15;
  gl_FragColor=vec4(c,1.0);}`;
const PVS = `attribute vec3 pos; attribute vec3 col; uniform mat4 mvp;
varying vec3 vc; void main(){ vc=col; gl_Position=mvp*vec4(pos,1.0); gl_PointSize=7.0; }`;
const PFS = `precision mediump float; varying vec3 vc;
void main(){ vec2 q=gl_PointCoord-0.5; if(dot(q,q)>0.25) discard; gl_FragColor=vec4(vc,1.0); }`;

function sh(t,s){const h=gl.createShader(t);gl.shaderSource(h,s);gl.compileShader(h);
  if(!gl.getShaderParameter(h,gl.COMPILE_STATUS))console.error(gl.getShaderInfoLog(h));return h;}
function prog(vs,fs){const p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,vs));
  gl.attachShader(p,sh(gl.FRAGMENT_SHADER,fs));gl.linkProgram(p);return p;}

const meshP = prog(VS,FS), ptP = prog(PVS,PFS);

// ---------- build one heightfield per layer ----------
const N = DATA.cells, SIZE = DATA.size, CX = SIZE/2;
function ramp(v, m){           // v/maxAbs in [-1,1] -> rgb
  const t = Math.max(-1, Math.min(1, v/m));
  const stops = t>=0
    ? [[0.00,[0.10,0.14,0.30]],[0.25,[0.00,0.55,0.85]],[0.55,[0.15,0.90,0.75]],[0.80,[1.00,0.85,0.25]],[1.00,[1.00,0.20,0.25]]]
    : [[0.00,[0.10,0.14,0.30]],[0.50,[0.35,0.10,0.55]],[1.00,[0.85,0.15,0.75]]];
  const u = Math.abs(t);
  for(let i=1;i<stops.length;i++)
    if(u<=stops[i][0]||i===stops.length-1){
      const [a,ca]=stops[i-1],[b,cb]=stops[i],f=b>a?(u-a)/(b-a):0;
      return [ca[0]+(cb[0]-ca[0])*f, ca[1]+(cb[1]-ca[1])*f, ca[2]+(cb[2]-ca[2])*f];
    }
}
const meshes = DATA.layers.map((L,li)=>{
  const vals = L.values;
  let mn=0,mx=0,nz=0;
  for(const v of vals){ if(v<mn)mn=v; if(v>mx)mx=v; if(v!==0)nz++; }
  const m = Math.max(1,Math.max(mx,-mn));
  const pos=new Float32Array(N*N*3), col=new Float32Array(N*N*3), nrm=new Float32Array(N*N*3);
  for(let y=0;y<N;y++)for(let x=0;x<N;x++){
    const i=y*N+x, v=vals[i];
    pos[i*3]=(x+0.5)-CX; pos[i*3+1]=0; pos[i*3+2]=(y+0.5)-CX;
    const c=ramp(v,m); col.set(c,i*3);
  }
  const idx=[],lidx=[];
  for(let y=0;y<N-1;y++)for(let x=0;x<N-1;x++){
    const i=y*N+x;
    idx.push(i,i+1,i+N, i+1,i+N+1,i+N);
    lidx.push(i,i+1, i,i+N);
  }
  return {vals,pos,col,nrm,idx:new Uint32Array(idx),lidx:new Uint32Array(lidx),mn,mx,nz,
          vbo:null,cbo:null,nbo:null,ibo:null,libo:null,color:L.color,name:L.name};
});

let HS = 1.0;
function normals(mesh){
  const {vals,pos,nrm}=mesh;
  const h=x=>vals[x]*HS*0.06;
  for(let y=0;y<N;y++)for(let x=0;x<N;x++){
    const i=y*N+x;
    const hl=x>0?h(i-1):h(i), hr=x<N-1?h(i+1):h(i);
    const hd=y>0?h(i-N):h(i), hu=y<N-1?h(i+N):h(i);
    let nx=hl-hr, ny=2, nz=hd-hu;
    const l=Math.hypot(nx,ny,nz)||1;
    nrm[i*3]=nx/l; nrm[i*3+1]=ny/l; nrm[i*3+2]=nz/l;
    pos[i*3+1]=vals[i]*HS*0.06;
  }
}
function upload(mesh){
  normals(mesh);
  const g=gl;
  if(!mesh.vbo){mesh.vbo=g.createBuffer();mesh.cbo=g.createBuffer();mesh.nbo=g.createBuffer();
    mesh.ibo=g.createBuffer();mesh.libo=g.createBuffer();
    g.bindBuffer(g.ELEMENT_ARRAY_BUFFER,mesh.ibo);g.bufferData(g.ELEMENT_ARRAY_BUFFER,mesh.idx,g.STATIC_DRAW);
    g.bindBuffer(g.ELEMENT_ARRAY_BUFFER,mesh.libo);g.bufferData(g.ELEMENT_ARRAY_BUFFER,mesh.lidx,g.STATIC_DRAW);}
  g.bindBuffer(g.ARRAY_BUFFER,mesh.vbo);g.bufferData(g.ARRAY_BUFFER,mesh.pos,g.DYNAMIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,mesh.nbo);g.bufferData(g.ARRAY_BUFFER,mesh.nrm,g.DYNAMIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,mesh.cbo);g.bufferData(g.ARRAY_BUFFER,mesh.col,g.STATIC_DRAW);
}

// source markers per layer
function hex(h){return [parseInt(h.slice(1,3),16)/255,parseInt(h.slice(3,5),16)/255,parseInt(h.slice(5,7),16)/255];}
const markers = DATA.layers.map((L,li)=>{
  const pts=[],cls=[],c=hex(L.color);
  for(const s of DATA.sources) if(s.layer===li){ pts.push(s.x-CX,6,s.y-CX); cls.push(1,1,1); }
  const g=gl, p=g.createBuffer(), cp=g.createBuffer();
  g.bindBuffer(g.ARRAY_BUFFER,p);g.bufferData(g.ARRAY_BUFFER,new Float32Array(pts),g.STATIC_DRAW);
  g.bindBuffer(g.ARRAY_BUFFER,cp);g.bufferData(g.ARRAY_BUFFER,new Float32Array(cls),g.STATIC_DRAW);
  return {p,cp,n:pts.length/3};
});

// ---------- camera / input ----------
let yaw=0.7, pitch=0.85, dist=340, drag=false, lx=0, ly=0;
canvas.addEventListener('mousedown',e=>{drag=true;lx=e.clientX;ly=e.clientY;});
addEventListener('mouseup',()=>drag=false);
addEventListener('mousemove',e=>{if(!drag)return;yaw+=(e.clientX-lx)*0.006;pitch=Math.min(1.5,Math.max(0.1,pitch+(e.clientY-ly)*0.006));lx=e.clientX;ly=e.clientY;});
canvas.addEventListener('wheel',e=>{e.preventDefault();dist*=e.deltaY>0?1.09:0.92;dist=Math.min(1200,Math.max(60,dist));},{passive:false});

function matMul(a,b){const o=new Float32Array(16);for(let i=0;i<4;i++)for(let j=0;j<4;j++){let s=0;for(let k=0;k<4;k++)s+=a[k*4+j]*b[i*4+k];o[i*4+j]=s;}return o;}
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

// ---------- UI ----------
let active=0;
const layersEl=document.getElementById('layers');
DATA.layers.forEach((L,li)=>{
  const b=document.createElement('span');b.className='layer'+(li===0?' on':'');
  b.innerHTML=`<span class=""dot"" style=""background:${L.color}""></span>${L.name}`;
  b.onclick=()=>{active=li;document.querySelectorAll('.layer').forEach(e=>e.classList.remove('on'));b.classList.add('on');stats();};
  layersEl.appendChild(b);
});
const hs=document.getElementById('hs'),hsv=document.getElementById('hsv');
hs.oninput=()=>{HS=hs.value/100;hsv.textContent=HS.toFixed(1)+'x';upload(meshes[active]);};
document.getElementById('wire');
function stats(){
  const m=meshes[active];
  document.getElementById('stats').textContent=
    `layer «${m.name}»: ${m.nz.toLocaleString()} covered cells  min ${m.mn}  max ${m.mx}`;
}
stats();

// ---------- render ----------
function draw(){
  const w=canvas.width=canvas.clientWidth*devicePixelRatio|0;
  const h=canvas.height=canvas.clientHeight*devicePixelRatio|0;
  gl.viewport(0,0,w,h);
  gl.enable(gl.DEPTH_TEST);gl.clearColor(0.043,0.063,0.125,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
  if(document.getElementById('spin').checked&&!drag)yaw+=0.003;
  const ex=Math.cos(pitch)*Math.cos(yaw)*dist, ey=Math.sin(pitch)*dist, ez=Math.cos(pitch)*Math.sin(yaw)*dist;
  const mvp=matMul(persp(0.9,w/h,1,4000),look(ex,ey,ez,0,0,0));

  const m=meshes[active];
  if(!m.vbo)upload(m);
  gl.useProgram(meshP);
  gl.uniformMatrix4fv(gl.getUniformLocation(meshP,'mvp'),false,mvp);
  const pa=gl.getAttribLocation(meshP,'pos'),na=gl.getAttribLocation(meshP,'nrm'),ca=gl.getAttribLocation(meshP,'col');
  gl.bindBuffer(gl.ARRAY_BUFFER,m.vbo);gl.vertexAttribPointer(pa,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pa);
  gl.bindBuffer(gl.ARRAY_BUFFER,m.nbo);gl.vertexAttribPointer(na,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(na);
  gl.bindBuffer(gl.ARRAY_BUFFER,m.cbo);gl.vertexAttribPointer(ca,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(ca);
  const wire=document.getElementById('wire').checked;
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,wire?m.libo:m.ibo);
  gl.drawElements(wire?gl.LINES:gl.TRIANGLES,wire?m.lidx.length:m.idx.length,gl.UNSIGNED_INT,0);

  if(document.getElementById('mk').checked&&markers[active].n){
    gl.useProgram(ptP);
    gl.uniformMatrix4fv(gl.getUniformLocation(ptP,'mvp'),false,mvp);
    const pp=gl.getAttribLocation(ptP,'pos'),pc=gl.getAttribLocation(ptP,'col');
    gl.bindBuffer(gl.ARRAY_BUFFER,markers[active].p);gl.vertexAttribPointer(pp,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pp);
    gl.bindBuffer(gl.ARRAY_BUFFER,markers[active].cp);gl.vertexAttribPointer(pc,3,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(pc);
    gl.drawArrays(gl.POINTS,0,markers[active].n);
  }
  requestAnimationFrame(draw);
}
draw();
</script>
</body>
</html>";
}
