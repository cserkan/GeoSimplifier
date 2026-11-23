import numpy as np,pickle,sys,time
sys.path.insert(0,'/home/user/work'); sys.path.insert(0,'/home/user/work/hibrit')
import load,iges
from embreex import rtcore_scene as rtcs
from embreex.mesh_construction import TriangleMesh
ANG=45
V,B=load.load('/home/user/work/model.stl'); vis=np.load('hibrit/trihits.npy')>0
Vv=V[vis]; Bv=B[vis]
sc=rtcs.EmbreeScene(); TriangleMesh(sc,Vv.astype(np.float32))
# weld + edge adjacency
q=np.round(Vv.reshape(-1,3)/2e-6).astype(np.int64); bb=np.repeat(Bv,3)
uniq,inv=np.unique(np.column_stack([bb,q]),axis=0,return_inverse=True); inv=inv.reshape(-1,3)
P=np.zeros((len(uniq),3)); P[inv.ravel()]=Vv.reshape(-1,3)
good=(inv[:,0]!=inv[:,1])&(inv[:,1]!=inv[:,2])&(inv[:,0]!=inv[:,2]); tri=inv[good]; ti=np.where(good)[0]
N=np.cross(Vv[ti,1]-Vv[ti,0],Vv[ti,2]-Vv[ti,0]); N/=np.linalg.norm(N,axis=1)[:,None]+1e-30
E=np.concatenate([tri[:,[0,1]],tri[:,[1,2]],tri[:,[2,0]]]); E.sort(axis=1); T=np.tile(np.arange(len(tri)),3)
o=np.lexsort((E[:,1],E[:,0])); E=E[o]; T=T[o]
st=np.r_[True,~np.all(E[1:]==E[:-1],axis=1)]; gid=np.cumsum(st)-1; cnt=np.bincount(gid); first=np.where(st)[0]
ue=E[first]; t1=T[first]; has2=cnt>=2; t2=np.where(has2,T[np.minimum(first+1,len(T)-1)],0)
cosd=np.where(has2,np.einsum('ij,ij->i',N[t1],N[t2]),-1)
feat=(cnt==1)|(cnt>2)|(cosd<np.cos(np.radians(ANG)))
def chain(edges):
    from collections import defaultdict
    adj=defaultdict(list)
    for i,(a,b) in enumerate(edges): adj[a].append(i); adj[b].append(i)
    used=np.zeros(len(edges),bool); out=[]
    for s in range(len(edges)):
        if used[s]: continue
        used[s]=True; a,b=edges[s]; pts=[a,b]
        for end in (1,0):
            cur=pts[-1] if end else pts[0]
            while True:
                nx=[e for e in adj[cur] if not used[e]]
                if len(adj[cur])!=2 or not nx: break
                e=nx[0]; used[e]=True; x,y=edges[e]; cur=y if x==cur else x
                if end: pts.append(cur)
                else: pts.insert(0,cur)
        out.append(P[pts])
    return out
def rdp(X,tol=1e-4):
    keep=np.zeros(len(X),bool); keep[0]=keep[-1]=True; stck=[(0,len(X)-1)]
    while stck:
        i,j=stck.pop()
        if j<=i+1: continue
        a=X[i];d=X[j]-a;Ln=np.linalg.norm(d);Y=X[i+1:j]-a
        dist=np.linalg.norm(Y,axis=1) if Ln<1e-12 else np.linalg.norm(np.cross(Y,d/Ln),axis=1)
        k=np.argmax(dist)
        if dist[k]>tol: m=i+1+k; keep[m]=True; stck+=[(i,m),(m,j)]
    return X[keep]
views={'on':[0,-1,0],'arka':[0,1,0],'sag':[1,0,0],'sol':[-1,0,0],'ust':[0,0,1],'alt':[0,0,-1]}
for name,d in views.items():
    d=np.array(d,float); t=time.time()
    sil=has2&((N[t1]@d)*(N[t2]@d)<0)
    front=(N[t1]@d>0)|(has2&(N[t2]@d>0))|(cnt==1)
    sel=(feat&front)|sil
    ed=ue[sel]
    a=P[ed[:,0]]; b=P[ed[:,1]]; votes=np.zeros(len(ed))
    for s in (0.2,0.5,0.8):
        Q=a+(b-a)*s+d*3e-4
        h=sc.run(Q.astype(np.float32),np.tile(d,(len(Q),1)).astype(np.float32)); votes+=(h<0)
    ed=ed[votes>=2]
    lines=[rdp(X) for X in chain(ed)]
    lines=[X for X in lines if np.linalg.norm(np.diff(X,axis=0),axis=1).sum()>0.002]
    iges.write_iges('hibrit/6M31_gorunus_%s.igs'%name,lines)
    pickle.dump(lines,open('hibrit/vedges2_%s.pkl'%name,'wb'))
    print(name,len(lines),sum(len(X)-1 for X in lines),round(time.time()-t,1),flush=True)
