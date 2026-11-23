import numpy as np,sys
from embreex import rtcore_scene as rtcs
from embreex.mesh_construction import TriangleMesh
from PIL import Image
def loadstl(p):
    dt=np.dtype([('n','<f4',3),('v','<f4',(3,3)),('a','<u2')])
    with open(p,'rb') as f: f.read(80); n=np.frombuffer(f.read(4),'<u4')[0]; d=np.frombuffer(f.read(int(n)*50),dt)
    return d['v'].astype(np.float32)
V=loadstl(sys.argv[1]); out=sys.argv[2]
sc=rtcs.EmbreeScene(); TriangleMesh(sc,V)
N=np.cross(V[:,1]-V[:,0],V[:,2]-V[:,0]); N/=np.linalg.norm(N,axis=1)[:,None]+1e-20
lo=V.reshape(-1,3).min(0); hi=V.reshape(-1,3).max(0); c=(lo+hi)/2; R=np.linalg.norm(hi-lo)/2
d=np.array([-1,-1,-0.8]); d/=np.linalg.norm(d)
up=np.array([0,0,1.]); u=np.cross(d,up); u/=np.linalg.norm(u); v=np.cross(u,d)
W=1400; k=np.linspace(-R,R,W); gu,gv=np.meshgrid(k,-k)
O=c-d*R*3+np.outer(gu.ravel(),u)+np.outer(gv.ravel(),v)
h=sc.run(O.astype(np.float32),np.tile(d,(len(O),1)).astype(np.float32))
img=np.full(len(O),255.0); m=h>=0
img[m]=40+200*np.abs(N[h[m]]@d)
Image.fromarray(img.reshape(W,W).astype(np.uint8)).save(out)
