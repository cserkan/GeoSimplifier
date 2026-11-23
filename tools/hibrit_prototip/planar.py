import numpy as np,sys,time,pickle
sys.path.insert(0,'/home/user/work'); import load
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
V,B=load.load('/home/user/work/model.stl'); vis=np.load('hibrit/trihits.npy')>0
Vv=V[vis]; Bv=B[vis]; t=time.time()
q=np.round(Vv.reshape(-1,3)/2e-6).astype(np.int64); bb=np.repeat(Bv,3)
uniq,inv=np.unique(np.column_stack([bb,q]),axis=0,return_inverse=True); inv=inv.reshape(-1,3)
good=(inv[:,0]!=inv[:,1])&(inv[:,1]!=inv[:,2])&(inv[:,0]!=inv[:,2]); tri=inv[good]; ti=np.where(good)[0]
N=np.cross(Vv[ti,1]-Vv[ti,0],Vv[ti,2]-Vv[ti,0]); A=np.linalg.norm(N,axis=1)/2; N/=2*A[:,None]+1e-30
dd=np.einsum('ij,ij->i',N,Vv[ti,0])
E=np.concatenate([tri[:,[0,1]],tri[:,[1,2]],tri[:,[2,0]]]); E.sort(axis=1); T=np.tile(np.arange(len(tri)),3)
o=np.lexsort((E[:,1],E[:,0])); E=E[o]; T=T[o]
same=np.all(E[1:]==E[:-1],axis=1); i1=T[:-1][same]; i2=T[1:][same]
cop=(np.einsum('ij,ij->i',N[i1],N[i2])>np.cos(np.radians(0.5)))&(np.abs(dd[i1]-dd[i2])<5e-5)
g=coo_matrix((np.ones(cop.sum()),(i1[cop],i2[cop])),shape=(len(tri),)*2)
nc,lab=connected_components(g,directed=False)
area=np.bincount(lab,weights=A); cnt=np.bincount(lab)
big=cnt>=2
print('tris',len(tri),'regions',nc,'multi-tri regions',big.sum(),'tris in them',cnt[big].sum(),'area share',area[big].sum()/area.sum(),round(time.time()-t))
for th in (4,20,100):
    m=cnt>=th; print('regions >=%d tris:'%th,m.sum(),'tris',cnt[m].sum())
pickle.dump((ti,tri,lab),open('hibrit/planar.pkl','wb'))
