import numpy as np, time
def load(path='model.stl'):
    dt=np.dtype([('n','<f4',3),('v','<f4',(3,3)),('a','<u2')])
    with open(path,'rb') as f:
        f.read(80); n=np.frombuffer(f.read(4),'<u4')[0]
        d=np.frombuffer(f.read(int(n)*50),dt)
    return d['v'].astype(np.float64), d['a'].astype(np.int64)
def boundary_loops(V, B):
    """V (n,3,3) triangles, B body per tri. returns list of (body, pts array)"""
    t=time.time()
    q=np.round(V.reshape(-1,3)/2e-6).astype(np.int64)
    bb=np.repeat(B,3)
    key=np.column_stack([bb,q])
    uniq,inv=np.unique(key,axis=0,return_inverse=True)
    inv=inv.reshape(-1,3)
    P=V.reshape(-1,3)[np.unique(inv.ravel(),return_index=True)[1]]  # positions per vertex id
    # ensure P index aligns: rebuild
    P=np.zeros((len(uniq),3)); P[inv.ravel()]=V.reshape(-1,3)
    good=(inv[:,0]!=inv[:,1])&(inv[:,1]!=inv[:,2])&(inv[:,0]!=inv[:,2])
    tri=inv[good]
    E=np.concatenate([tri[:,[0,1]],tri[:,[1,2]],tri[:,[2,0]]])
    E.sort(axis=1)
    ue,cnt=np.unique(E,axis=0,return_counts=True)
    be=ue[cnt==1]
    print('weld',time.time()-t,'verts',len(P),'boundary edges',len(be))
    from collections import defaultdict
    adj=defaultdict(list)
    for i,(a,b) in enumerate(be): adj[a].append(i); adj[b].append(i)
    used=np.zeros(len(be),bool); loops=[]
    vbody=np.zeros(len(P),np.int64); vbody[inv.ravel()]=bb
    for s in range(len(be)):
        if used[s]: continue
        used[s]=True; start,cur=be[s]; pts=[start]; closed=False
        while True:
            if cur==start: closed=True; break
            pts.append(cur); nxt=-1
            for e in adj[cur]:
                if not used[e]: nxt=e; break
            if nxt<0: break
            used[nxt]=True; a,b=be[nxt]; cur=b if a==cur else a
        if closed and len(pts)>=3: loops.append((int(vbody[start]),np.array(pts)))
    print('loops',len(loops),time.time()-t)
    return P,tri,loops
