import numpy as np,pickle,sys
sys.path.insert(0,'/home/user/work'); sys.path.insert(0,'/home/user/work/hibrit')
import load,iges
V,B=load.load('/home/user/work/model.stl'); hits=np.load('hibrit/trihits.npy'); vis=hits>0
Vv=V[vis].astype(np.float32)*1000  # mm
dt=np.dtype([('n','<f4',3),('v','<f4',(3,3)),('a','<u2')])
a=np.zeros(len(Vv),dt); a['v']=Vv
n=np.cross(Vv[:,1]-Vv[:,0],Vv[:,2]-Vv[:,0]); a['n']=n/(np.linalg.norm(n,axis=1)[:,None]+1e-20)
with open('hibrit/6M31_gorunur_1.6M_mm.stl','wb') as f:
    f.write(b'3DWrap visible tris mm'.ljust(80)); f.write(np.uint32(len(a)).tobytes()); f.write(a.tobytes())
for ang in (30,45,60):
    L=pickle.load(open('hibrit/edges_%d.pkl'%ang,'rb'))
    iges.write_iges('hibrit/6M31_kenar_%d.igs'%ang,L)
print('ok')
