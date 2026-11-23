import numpy as np,sys,time,pyfqmr
sys.path.insert(0,'/home/user/work'); import load
V,B=load.load('/home/user/work/model.stl'); vis=np.load('hibrit/trihits.npy')>0
V=V[vis]*1000; B=B[vis]   # mm
order=np.argsort(B,kind='stable'); V=V[order]; B=B[order]
bounds=np.r_[0,np.where(np.diff(B))[0]+1,len(B)]
for eps in [float(x) for x in sys.argv[1:]]:
    t=time.time(); outV=[];outB=[]
    for i in range(len(bounds)-1):
        Vi=V[bounds[i]:bounds[i+1]]
        if len(Vi)<20: outV.append(Vi); outB.append(np.full(len(Vi),B[bounds[i]])); continue
        q=np.round(Vi.reshape(-1,3)/2e-3).astype(np.int64)
        u,inv=np.unique(q,axis=0,return_inverse=True)
        P=np.zeros((len(u),3)); P[inv.ravel()]=Vi.reshape(-1,3); F=inv.reshape(-1,3)
        s=pyfqmr.Simplify(); s.setMesh(P,F)
        s.simplify_mesh_lossless(epsilon=eps,preserve_border=False,verbose=0)
        P2,F2,_=s.getMesh(); outV.append(P2[F2]); outB.append(np.full(len(F2),B[bounds[i]]))
    Vo=np.concatenate(outV).astype(np.float32); Bo=np.concatenate(outB)
    dt=np.dtype([('n','<f4',3),('v','<f4',(3,3)),('a','<u2')]); a=np.zeros(len(Vo),dt); a['v']=Vo; a['a']=(Bo%65535).astype(np.uint16)
    n=np.cross(Vo[:,1]-Vo[:,0],Vo[:,2]-Vo[:,0]); a['n']=n/(np.linalg.norm(n,axis=1)[:,None]+1e-20)
    fn='hibrit/6M31_gorunur_eps%g_%dk_mm.stl'%(eps,len(Vo)//1000)
    with open(fn,'wb') as f: f.write(b'3DWrap visible lossless mm'.ljust(80)); f.write(np.uint32(len(a)).tobytes()); f.write(a.tobytes())
    print(fn,len(Vo),round(time.time()-t),flush=True)
