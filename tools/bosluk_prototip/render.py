import numpy as np
from PIL import Image
def render(groups, center, radius, direction=(1,1,1), up=(0,0,1), size=700, path='out.png'):
    """groups: list of (tris (k,3,3), rgb). orthographic flat-shaded z-buffer."""
    d=np.array(direction,float); d/=np.linalg.norm(d)
    upv=np.array(up,float); r=np.cross(upv,d)
    if np.linalg.norm(r)<1e-6: r=np.cross([0,1,0],d)
    r/=np.linalg.norm(r); u=np.cross(d,r)
    img=np.full((size,size,3),245,np.uint8); z=np.full((size,size),-np.inf)
    light=d*0.6+u*0.3+r*0.2; light/=np.linalg.norm(light)
    s=size/(2*radius)
    for T,rgb in groups:
        if len(T)==0: continue
        rel=T-center
        X=rel@r*s+size/2; Y=size/2-rel@u*s; Z=rel@d
        nrm=np.cross(T[:,1]-T[:,0],T[:,2]-T[:,0]); nl=np.linalg.norm(nrm,axis=1); nl[nl==0]=1
        shade=np.abs((nrm/nl[:,None])@light)*0.75+0.25
        for i in range(len(T)):
            x,y,zz=X[i],Y[i],Z[i]
            x0=max(int(np.floor(x.min())),0); x1=min(int(np.ceil(x.max())),size-1)
            y0=max(int(np.floor(y.min())),0); y1=min(int(np.ceil(y.max())),size-1)
            if x0>x1 or y0>y1: continue
            gx,gy=np.meshgrid(np.arange(x0,x1+1)+0.5,np.arange(y0,y1+1)+0.5)
            den=(y[1]-y[2])*(x[0]-x[2])+(x[2]-x[1])*(y[0]-y[2])
            if abs(den)<1e-12: continue
            a=((y[1]-y[2])*(gx-x[2])+(x[2]-x[1])*(gy-y[2]))/den
            b=((y[2]-y[0])*(gx-x[2])+(x[0]-x[2])*(gy-y[2]))/den
            c=1-a-b
            m=(a>=-1e-6)&(b>=-1e-6)&(c>=-1e-6)
            if not m.any(): continue
            zv=a*zz[0]+b*zz[1]+c*zz[2]
            sub=z[y0:y1+1,x0:x1+1]; upd=m&(zv>sub)
            sub[upd]=zv[upd]
            col=(np.array(rgb)*shade[i]).astype(np.uint8)
            img[y0:y1+1,x0:x1+1][upd]=col
    Image.fromarray(img).save(path)
