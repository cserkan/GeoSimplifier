import numpy as np,time,sys
sys.path.insert(0,'/home/user/work')
import load
ANG=float(sys.argv[1]) if len(sys.argv)>1 else 30
TOL=float(sys.argv[2]) if len(sys.argv)>2 else 1e-4   # RDP tol (m)
MINLEN=float(sys.argv[3]) if len(sys.argv)>3 else 0.005  # drop polylines shorter (m)
V,B=load.load('/home/user/work/model.stl')
hits=np.load('/home/user/work/hibrit/trihits.npy'); vis=hits>0
t=time.time()
q=np.round(V.reshape(-1,3)/2e-6).astype(np.int64); bb=np.repeat(B,3)
uniq,inv=np.unique(np.column_stack([bb,q]),axis=0,return_inverse=True); inv=inv.reshape(-1,3)
P=np.zeros((len(uniq),3)); P[inv.ravel()]=V.reshape(-1,3)
good=(inv[:,0]!=inv[:,1])&(inv[:,1]!=inv[:,2])&(inv[:,0]!=inv[:,2])
ti=np.where(good)[0]; tri=inv[good]
N=np.cross(V[ti,1]-V[ti,0],V[ti,2]-V[ti,0]); N/=np.linalg.norm(N,axis=1)[:,None]+1e-30
E=np.concatenate([tri[:,[0,1]],tri[:,[1,2]],tri[:,[2,0]]]); E.sort(axis=1)
T=np.tile(np.arange(len(tri)),3)
o=np.lexsort((E[:,1],E[:,0])); E=E[o]; T=T[o]
same=np.all(E[1:]==E[:-1],axis=1)
# start of groups
st=np.r_[True,~same]; gid=np.cumsum(st)-1; cnt=np.bincount(gid)
first=np.where(st)[0]
ue=E[first]; c=cnt
t1=T[first]; t2=np.where(c>=2,T[np.minimum(first+1,len(T)-1)],-1)
visE=vis[ti[t1]]|np.where(t2>=0,vis[ti[np.maximum(t2,0)]],False)
cosd=np.where(t2>=0,np.einsum('ij,ij->i',N[t1],N[np.maximum(t2,0)]),-1)
sharp=(c==1)|(c>2)|(cosd<np.cos(np.radians(ANG)))
sel=sharp&visE
fe=ue[sel]
print('edges',len(ue),'feature visible',len(fe),round(time.time()-t))
# chain
from collections import defaultdict
adj=defaultdict(list)
for i,(a,b) in enumerate(fe): adj[a].append(i); adj[b].append(i)
used=np.zeros(len(fe),bool); lines=[]
deg={k:len(v) for k,v in adj.items()}
def walk(s,start,cur):
    pts=[start,cur]
    while deg[cur]==2 and cur!=start:
        nxt=-1
        for e in adj[cur]:
            if not used[e]: nxt=e;break
        if nxt<0: break
        used[nxt]=True; a,b=fe[nxt]; cur=b if a==cur else a; pts.append(cur)
    return pts
order=sorted(range(len(fe)),key=lambda i: -(deg[fe[i][0]]!=2 or deg[fe[i][1]]!=2))
for s in order:
    if used[s]: continue
    used[s]=True; a,b=fe[s]
    if deg[a]!=2: pts=walk(s,a,b)
    elif deg[b]!=2: pts=walk(s,b,a)
    else: pts=walk(s,a,b)
    lines.append(np.array(pts))
def rdp(X,tol):
    keep=np.zeros(len(X),bool); keep[0]=keep[-1]=True; st=[(0,len(X)-1)]
    while st:
        i,j=st.pop()
        if j<=i+1: continue
        a=X[i];b=X[j];d=b-a;L=np.linalg.norm(d)
        Y=X[i+1:j]-a
        if L<1e-12: dist=np.linalg.norm(Y,axis=1)
        else: dist=np.linalg.norm(np.cross(Y,d/L),axis=1)
        k=np.argmax(dist)
        if dist[k]>tol: m=i+1+k; keep[m]=True; st+= [(i,m),(m,j)]
    return X[keep]
out=[]; segs=0
for L in lines:
    X=P[L]
    ln=np.linalg.norm(np.diff(X,axis=0),axis=1).sum()
    if ln<MINLEN: continue
    Y=rdp(X,TOL); out.append(Y); segs+=len(Y)-1
print('polylines',len(out),'segments',segs,round(time.time()-t))
import pickle; pickle.dump(out,open('/home/user/work/hibrit/edges_%d.pkl'%int(ANG),'wb'))
