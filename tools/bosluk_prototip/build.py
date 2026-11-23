import patch,clean,numpy as np,pickle,time,collections
P,tri,matched=pickle.load(open('state.pkl','rb'))
want={'delik-duz','agiz-duz','dolgu'}
# consistency: komsu-kapali / yarik loops in a body that look like >=3 filled holes of the same body get filled too
by=collections.defaultdict(list)
for m in matched: by[m[0]].append(m)
def sig(X):
    c,n,u,v=patch.plane_fit(X); p=np.column_stack([(X-c)@u,(X-c)@v])
    return abs(patch.poly_area(p)), np.linalg.norm(np.roll(X,-1,0)-X,axis=1).sum()
extra=0; todo=[]
for b,l in by.items():
    filled=[m for m in l if m[2] in want]
    sigs=[sig(P[m[1]]) for m in filled]
    for m in l:
        if m[2] in want: todo.append(m); continue
        if m[2] in ('komsu-kapali','yarik') and len(sigs)>=3:
            a,L=sig(P[m[1]])
            sim=sum(1 for (a2,L2) in sigs if abs(a2-a)<0.1*a2 and abs(L2-L)<0.1*L2)
            if sim>=3:
                todo.append((m[0],m[1],'delik-duz' if np.abs((P[m[1]]-P[m[1]].mean(0))@patch.plane_fit(P[m[1]])[1]).max()<max(3e-4,0.02*m[4]/1000) else 'dolgu',m[3],m[4])); extra+=1
print('ek (tutarlılık):',extra,'toplam aday',len(todo))
t=time.time(); patches=[]; fail=collections.Counter(); devs=[]
for body,idx,cl,lid,size in todo:
    X=P[idx]; out=patch.fill_patch(X)
    if out is None:
        out=(None,None,0,patch.plane_fit(X),None); cl='dolgu'
    verts,tr,nb,frame,p2=out
    p={'id':lid,'body':body,'cls':cl,'X':X,'verts':verts,'tris':tr,'frame':frame,'size':size}
    if cl!='dolgu':
        p['kind']='plane'
        cx,cy,r,rr=patch.circle_fit(p2)
        sz=size/1000
        p['circle']=(cx,cy,r) if len(X)>=8 and rr<max(0.005*r,2e-5) and 0.3*sz<r<0.6*sz and patch.inside(p2,np.array([[cx,cy]]))[0] else None
    else:
        p['kind']='coons'
        best=None
        for nn in (6,9,12,16):
            r_=clean.coons(X,n=nn)
            if r_ is None: continue
            if best is None or r_[2]<best[2]: best=r_
            if r_[2]<5e-5: break
        if best is None: fail[cl]+=1; continue
        p['coons']=(best[0],best[1]); p['bdev']=best[2]; devs.append((best[2],best[2]))
        patches.append(p); continue
        p['kind']='bspline'
        best=None
        for nn in (4,6,9,12):
            pass
            ctrl,ku,kv,(dev,bdev)=clean.fit_bspline(verts,frame,nn,nn,nb=nb)
            best=(ctrl,ku,kv,dev,bdev,clean.last_fit.copy())
            if bdev<5e-5: break
        ctrl,ku,kv,dev,bdev,fb=best
        p['X']=fb
        p['bs']=(ctrl,ku,kv); p['dev']=dev; p['bdev']=bdev; devs.append((dev,bdev))
    patches.append(p)
print('yama',len(patches),'başarısız',dict(fail),round(time.time()-t,1),'sn')
print(collections.Counter(p['cls'] for p in patches),'daire',sum(1 for p in patches if p.get('circle')))
d=np.array(devs)*1000
for k,nm in ((0,'yama'),(1,'kenar')): print('NURBS %s sapma mm: medyan %.3f p90 %.3f max %.3f'%(nm,np.median(d[:,k]),np.percentile(d[:,k],90),d[:,k].max()))
pickle.dump(patches,open('patches.pkl','wb'))
cols={'delik-duz':(255,120,0),'agiz-duz':(235,190,0),'dolgu':(215,0,215)}
t=time.time(); clean.write_step('3DWrap_6M31_yamalar.step',patches,cols); print('step',round(time.time()-t,1),'sn')
