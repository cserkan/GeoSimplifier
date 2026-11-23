import numpy as np,time,sys
sys.path.insert(0,'/home/user/work')
import load
from embreex import rtcore_scene as rtcs
from embreex.mesh_construction import TriangleMesh
V,B=load.load('/home/user/work/model.stl')
print('tris',len(V))
sc=rtcs.EmbreeScene(); TriangleMesh(sc,V.astype(np.float32))
lo=V.reshape(-1,3).min(0); hi=V.reshape(-1,3).max(0); c=(lo+hi)/2; R=np.linalg.norm(hi-lo)/2*1.02
ndir=int(sys.argv[1]) if len(sys.argv)>1 else 200; step=float(sys.argv[2]) if len(sys.argv)>2 else 0.002
hits=np.zeros(len(V),np.int32)
g=(np.arange(ndir)+0.5)/ndir; phi=np.arccos(1-2*g); th=np.pi*(1+5**0.5)*np.arange(ndir)
dirs=np.column_stack([np.sin(phi)*np.cos(th),np.sin(phi)*np.sin(th),np.cos(phi)])
k=np.arange(-R,R,step); t=time.time()
for i,d in enumerate(dirs):
    a=np.array([1,0,0]) if abs(d[0])<0.9 else np.array([0,1,0]); u=np.cross(d,a); u/=np.linalg.norm(u); v=np.cross(d,u)
    jit=(np.random.rand(2)-0.5)*step
    gu,gv=np.meshgrid(k+jit[0],k+jit[1]); O=c-d*R*1.5+np.outer(gu.ravel(),u)+np.outer(gv.ravel(),v)
    h=sc.run(O.astype(np.float32),np.tile(d,(len(O),1)).astype(np.float32))
    h=h[h>=0]; np.add.at(hits,h,1)
    if i%20==0: print(i,round(time.time()-t),(hits>0).sum(),flush=True)
np.save('/home/user/work/hibrit/trihits.npy',hits)
print('visible tris',(hits>0).sum(),'of',len(V),'time',round(time.time()-t))
