import numpy as np, csv, time, pickle
from scipy.spatial import Delaunay, cKDTree
import scipy.sparse as sp, scipy.sparse.linalg as spl

def read_classes(path='loops.csv'):
    rows=list(csv.DictReader(open(path,encoding='utf-8-sig'),delimiter=';'))
    out={}
    for r in rows:
        parts=[r['merkez_mm']]+list(r.get(None) or [])
        c=[float(x.strip('()').replace(',','.')) for x in parts[:3]]
        out.setdefault(int(r['govde']),[]).append((np.array(c)/1000,r))
    return out

def plane_fit(X):
    c=X.mean(0); u,s,vt=np.linalg.svd(X-c,full_matrices=False); n=vt[2]
    # Newell for orientation robustness
    nw=np.zeros(3)
    for i in range(len(X)):
        a=X[i]; b=X[(i+1)%len(X)]
        nw+=[(a[1]-b[1])*(a[2]+b[2]),(a[2]-b[2])*(a[0]+b[0]),(a[0]-b[0])*(a[1]+b[1])]
    if np.linalg.norm(nw)>1e-15:
        nwu=nw/np.linalg.norm(nw)
        if abs(nwu@n)<0.5: n=nwu
        elif nwu@n<0: n=-n
    a=np.array([1,0,0]) if abs(n[0])<0.9 else np.array([0,1,0])
    u=np.cross(a,n); u/=np.linalg.norm(u); v=np.cross(n,u)
    return c,n,u,v

def poly_area(p):
    x,y=p[:,0],p[:,1]; return 0.5*(x@np.roll(y,-1)-y@np.roll(x,-1))

def simple_polygon(p):
    # check no self intersection (O(n^2) on small n), approximate
    n=len(p)
    if n>400: return True
    A=p; Bp=np.roll(p,-1,0)
    for i in range(n):
        a,b=A[i],Bp[i]
        for j in range(i+2,n):
            if i==0 and j==n-1: continue
            c,d=A[j],Bp[j]
            def o(p1,p2,p3): return (p2[0]-p1[0])*(p3[1]-p1[1])-(p2[1]-p1[1])*(p3[0]-p1[0])
            if o(a,b,c)*o(a,b,d)<0 and o(c,d,a)*o(c,d,b)<0: return False
    return True

def inside(poly,pts):
    x,y=pts[:,0],pts[:,1]; res=np.zeros(len(pts),bool)
    xi,yi=poly[:,0],poly[:,1]; xj,yj=np.roll(xi,1),np.roll(yi,1)
    for a,b,c,d in zip(xi,yi,xj,yj):
        m=((b>y)!=(d>y))
        with np.errstate(divide='ignore',invalid='ignore'):
            xc=(c-a)*(y-b)/(d-b)+a
        res^=m&(x<xc)
    return res

def fill_patch(X, interior_step=None):
    """X: loop points (n,3). returns verts (m,3), tris (k,3), nb boundary count, plane frame"""
    c,n,u,v=plane_fit(X)
    p=np.column_stack([(X-c)@u,(X-c)@v])
    if not simple_polygon(p): return None
    L=np.linalg.norm(np.roll(X,-1,0)-X,axis=1).sum()
    step=interior_step or max(L/len(X)*1.5, 1e-4)
    lo,hi=p.min(0),p.max(0)
    gx=np.arange(lo[0]+step/2,hi[0],step); gy=np.arange(lo[1]+step/2,hi[1],step)
    G=np.array(np.meshgrid(gx,gy)).reshape(2,-1).T if len(gx) and len(gy) else np.zeros((0,2))
    if len(G):
        G=G[inside(p,G)]
        if len(G):
            d,_=cKDTree(p).query(G); G=G[d>0.6*step]
    allp=np.vstack([p,G])
    try: tri=Delaunay(allp).simplices
    except Exception: return None
    cen=allp[tri].mean(1)
    tri=tri[inside(p,cen)]
    if len(tri)==0: return None
    nb=len(p)
    # heights: boundary from X (offset along n), interior by harmonic (membrane) smoothing, then biharmonic-ish 2 passes
    h=np.zeros(len(allp)); h[:nb]=(X-c)@n
    m=len(allp)
    if m>nb:
        I=[];J=[]
        for a,b in ((0,1),(1,2),(2,0)):
            I+=list(tri[:,a]);J+=list(tri[:,b])
        W=sp.coo_matrix((np.ones(len(I)*2),(I+J,J+I)),shape=(m,m)).tocsr()
        W.data[:]=1
        D=np.asarray(W.sum(1)).ravel()
        Lap=sp.diags(D)-W
        free=np.arange(nb,m); fix=np.arange(nb)
        A=Lap[free][:,free]; b=-Lap[free][:,fix]@h[fix]
        try: h[free]=spl.spsolve(A.tocsc(),b)
        except Exception: pass
    verts=c+np.outer(allp[:,0],u)+np.outer(allp[:,1],v)+np.outer(h,n)
    return verts,tri,nb,(c,n,u,v),p

def circle_fit(p2):
    x,y=p2[:,0],p2[:,1]
    A=np.column_stack([x,y,np.ones_like(x)]); b=x*x+y*y
    s,*_=np.linalg.lstsq(A,b,rcond=None)
    cx,cy=s[0]/2,s[1]/2; r=np.sqrt(max(s[2]+cx*cx+cy*cy,0))
    res=np.abs(np.hypot(x-cx,y-cy)-r).max()
    return cx,cy,r,res
