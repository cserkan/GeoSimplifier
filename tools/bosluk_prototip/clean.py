import numpy as np
def clamped_knots(nctrl,deg=3):
    inner=nctrl-deg-1
    return np.concatenate([np.zeros(deg+1),np.linspace(0,1,inner+2)[1:-1],np.ones(deg+1)])
def basis(knots,deg,n,t):
    t=np.clip(t,0,1-1e-12)
    N=np.zeros((len(t),len(knots)-1))
    for i in range(len(knots)-1):
        N[:,i]=(knots[i]<=t)&(t<knots[i+1])
    for k in range(1,deg+1):
        Nn=np.zeros((len(t),len(knots)-1-k))
        for i in range(len(knots)-1-k):
            d1=knots[i+k]-knots[i]; d2=knots[i+k+1]-knots[i+1]
            a=(t-knots[i])/d1*N[:,i] if d1>0 else 0
            b=(knots[i+k+1]-t)/d2*N[:,i+1] if d2>0 else 0
            Nn[:,i]=a+b
        N=Nn
    return N[:,:n]
def fit_bspline(verts,frame,nu=6,nv=6,lam=1e-6,nb=0,wb=8.0):
    c,n,u,v=frame
    rel=verts-c; x=rel@u; y=rel@v
    lo=np.array([x.min(),y.min()]); hi=np.array([x.max(),y.max()]); ext=(hi-lo)*0.05+1e-6
    lo-=ext; hi+=ext
    s=(x-lo[0])/(hi[0]-lo[0]); t=(y-lo[1])/(hi[1]-lo[1])
    ku,kv=clamped_knots(nu),clamped_knots(nv)
    Bu=basis(ku,3,nu,s); Bv=basis(kv,3,nv,t)
    A=(Bu[:,:,None]*Bv[:,None,:]).reshape(len(s),-1)
    # smoothing: second differences in both directions
    R=[]
    for i in range(nu):
        for j in range(nv):
            if 0<i<nu-1:
                r=np.zeros(nu*nv); r[(i-1)*nv+j]=1; r[i*nv+j]=-2; r[(i+1)*nv+j]=1; R.append(r)
            if 0<j<nv-1:
                r=np.zeros(nu*nv); r[i*nv+j-1]=1; r[i*nv+j]=-2; r[i*nv+j+1]=1; R.append(r)
    R=np.array(R)
    w=np.ones(len(s)); w[:nb]=wb
    M=np.vstack([A*w[:,None],np.sqrt(lam)*R*len(s)**0.5])
    ctrl=np.zeros((nu*nv,3))
    for k in range(3):
        rhs=np.concatenate([verts[:,k]*w,np.zeros(len(R))])
        ctrl[:,k]=np.linalg.lstsq(M,rhs,rcond=None)[0]
    err=np.linalg.norm(A@ctrl-verts,axis=1)
    globals()['last_fit']=(A@ctrl)[:nb] if nb else None
    return ctrl.reshape(nu,nv,3),ku,kv,(err.max(), err[:nb].max() if nb else 0)

class Step:
    def __init__(s): s.lines=[]; s.n=0
    def add(s,txt): s.n+=1; s.lines.append(f'#{s.n}={txt};'); return s.n
    def pt(s,p): return s.add("CARTESIAN_POINT('',(%.6f,%.6f,%.6f))"%tuple(np.asarray(p)*1000))
    def dr(s,d): d=np.asarray(d,float); d=d/np.linalg.norm(d); return s.add("DIRECTION('',(%.9f,%.9f,%.9f))"%tuple(d))
    def ax(s,o,z,x): return s.add(f"AXIS2_PLACEMENT_3D('',#{s.pt(o)},#{s.dr(z)},#{s.dr(x)})")

def write_step(path,patches,colors):
    S=Step()
    ctx_lines=[]
    # styles per colour
    style={}
    for k,rgb in colors.items():
        c=S.add("COLOUR_RGB('',%.3f,%.3f,%.3f)"%tuple(np.array(rgb)/255))
        f=S.add(f"FILL_AREA_STYLE_COLOUR('',#{c})"); fa=S.add(f"FILL_AREA_STYLE('',(#{f}))")
        sf=S.add(f"SURFACE_STYLE_FILL_AREA(#{fa})"); ss=S.add(f"SURFACE_SIDE_STYLE('',(#{sf}))")
        su=S.add(f"SURFACE_STYLE_USAGE(.BOTH.,#{ss})"); style[k]=S.add(f"PRESENTATION_STYLE_ASSIGNMENT((#{su}))")
    shells=[]; styled=[]
    for p in patches:
        loop=p['X']
        if p['kind']=='coons':
            ctrl,kk=p['coons']; n_=ctrl.shape[0]
            vals,mult=np.unique(np.round(kk,12),return_counts=True)
            mu='('+','.join(str(m) for m in mult)+')'; kv_='('+','.join('%.9f'%x for x in vals)+')'
            pid=[[S.pt(ctrl[i,j]) for j in range(n_)] for i in range(n_)]
            rows=['('+','.join('#%d'%pid[i][j] for j in range(n_))+')' for i in range(n_)]
            surf=S.add(f"B_SPLINE_SURFACE_WITH_KNOTS('',3,3,({','.join(rows)}),.UNSPECIFIED.,.F.,.F.,.F.,{mu},{mu},{kv_},{kv_},.UNSPECIFIED.)")
            corners=[pid[0][0],pid[-1][0],pid[-1][-1],pid[0][-1]]
            vps=[S.add(f"VERTEX_POINT('',#{c})") for c in corners]
            def bcurve(ids): return S.add("B_SPLINE_CURVE_WITH_KNOTS('',3,("+','.join('#%d'%q for q in ids)+f"),.UNSPECIFIED.,.F.,.F.,{mu},{kv_},.UNSPECIFIED.)")
            e1=S.add(f"EDGE_CURVE('',#{vps[0]},#{vps[1]},#{bcurve([pid[i][0] for i in range(n_)])},.T.)")
            e2=S.add(f"EDGE_CURVE('',#{vps[1]},#{vps[2]},#{bcurve([pid[-1][j] for j in range(n_)])},.T.)")
            e3=S.add(f"EDGE_CURVE('',#{vps[3]},#{vps[2]},#{bcurve([pid[i][-1] for i in range(n_)])},.T.)")
            e4=S.add(f"EDGE_CURVE('',#{vps[0]},#{vps[3]},#{bcurve([pid[0][j] for j in range(n_)])},.T.)")
            oes=[S.add(f"ORIENTED_EDGE('',*,*,#{e1},.T.)"),S.add(f"ORIENTED_EDGE('',*,*,#{e2},.T.)"),S.add(f"ORIENTED_EDGE('',*,*,#{e3},.F.)"),S.add(f"ORIENTED_EDGE('',*,*,#{e4},.F.)")]
            el=S.add("EDGE_LOOP('',("+','.join('#%d'%o for o in oes)+"))")
            fb=S.add(f"FACE_OUTER_BOUND('',#{el},.T.)")
            face=S.add(f"ADVANCED_FACE('yama_{p['id']}',(#{fb}),#{surf},.T.)")
            shells.append(S.add(f"OPEN_SHELL('',(#{face}))"))
            styled.append(S.add(f"STYLED_ITEM('',(#{style[p['cls']]}),#{face})"))
            continue
        if p['kind']=='plane':
            c,n,u,v=p['frame']
            surf=S.add(f"PLANE('',#{S.ax(c,n,u)})")
        else:
            ctrl,ku,kv=p['bs']
            nu,nv=ctrl.shape[:2]
            rows=[]
            for i in range(nu):
                rows.append('('+','.join('#%d'%S.pt(ctrl[i,j]) for j in range(nv))+')')
            def kn(k):
                vals,mult=np.unique(np.round(k,12),return_counts=True)
                return '('+','.join(str(m) for m in mult)+')','('+','.join('%.9f'%x for x in vals)+')'
            mu,ku_=kn(ku); mv,kv_=kn(kv)
            surf=S.add(f"B_SPLINE_SURFACE_WITH_KNOTS('',3,3,({','.join(rows)}),.UNSPECIFIED.,.F.,.F.,.F.,{mu},{mv},{ku_},{kv_},.UNSPECIFIED.)")
        if p.get('circle') is not None:
            cx,cy,r=p['circle']; c,n,u,v=p['frame']
            cen=c+cx*u+cy*v; start=cen+r*u
            curve=S.add(f"CIRCLE('',#{S.ax(cen,n,u)},{r*1000:.6f})")
            vp=S.add(f"VERTEX_POINT('',#{S.pt(start)})")
            ec=S.add(f"EDGE_CURVE('',#{vp},#{vp},#{curve},.T.)")
            oes=[S.add(f"ORIENTED_EDGE('',*,*,#{ec},.T.)")]
        else:
            pts=[S.pt(q) for q in loop]+[None]
            pts[-1]=pts[0]
            curve=S.add("POLYLINE('',("+','.join('#%d'%q for q in pts)+"))")
            vp=S.add(f"VERTEX_POINT('',#{pts[0]})")
            ec=S.add(f"EDGE_CURVE('',#{vp},#{vp},#{curve},.T.)")
            oes=[S.add(f"ORIENTED_EDGE('',*,*,#{ec},.T.)")]
        el=S.add("EDGE_LOOP('',("+','.join('#%d'%o for o in oes)+"))")
        fb=S.add(f"FACE_OUTER_BOUND('',#{el},.T.)")
        face=S.add(f"ADVANCED_FACE('yama_{p['id']}',(#{fb}),#{surf},.T.)")
        shells.append(S.add(f"OPEN_SHELL('',(#{face}))"))
        styled.append(S.add(f"STYLED_ITEM('',(#{style[p['cls']]}),#{face})"))
    sbsm=S.add("SHELL_BASED_SURFACE_MODEL('',("+','.join('#%d'%x for x in shells)+"))")
    # context
    lu=S.add("( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )")
    au=S.add("( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) )")
    su_=S.add("( NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT() )")
    unc=S.add(f"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-03),#{lu},'distance_accuracy_value','')")
    ctx=S.add(f"( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{unc})) GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lu},#{au},#{su_})) REPRESENTATION_CONTEXT('','') )")
    orig=S.ax(np.zeros(3),[0,0,1],[1,0,0])
    rep=S.add(f"MANIFOLD_SURFACE_SHAPE_REPRESENTATION('3DWrap_yamalar',(#{orig},#{sbsm}),#{ctx})")
    S.add(f"MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION('',("+','.join('#%d'%x for x in styled)+f"),#{ctx})")
    ac=S.add("APPLICATION_CONTEXT('automotive_design')")
    S.add(f"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#{ac})")
    pc=S.add(f"PRODUCT_CONTEXT('',#{ac},'mechanical')")
    prod=S.add(f"PRODUCT('3DWrap_yamalar','3DWrap_yamalar','',(#{pc}))")
    S.add(f"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,(#{prod}))")
    pdf=S.add(f"PRODUCT_DEFINITION_FORMATION('','',#{prod})")
    pdc=S.add(f"PRODUCT_DEFINITION_CONTEXT('part definition',#{ac},'design')")
    pd=S.add(f"PRODUCT_DEFINITION('design','',#{pdf},#{pdc})")
    pds=S.add(f"PRODUCT_DEFINITION_SHAPE('','',#{pd})")
    S.add(f"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{rep})")
    hdr=("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('3DWrap bosluk yamalari'),'2;1');\n"
         "FILE_NAME('3DWrap_yamalar.step','2026-11-23T00:00:00',(''),(''),'3DWrap','3DWrap','');\n"
         "FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n")
    open(path,'w').write(hdr+'\n'.join(S.lines)+"\nENDSEC;\nEND-ISO-10303-21;\n")

def fit_curve(pts,n,deg=3):
    """clamped cubic B-spline with n ctrl through endpoints, least squares on chord-length params"""
    d=np.r_[0,np.cumsum(np.linalg.norm(np.diff(pts,axis=0),axis=1))]; t=d/d[-1] if d[-1]>0 else np.linspace(0,1,len(pts))
    k=clamped_knots(n,deg); B=basis(k,deg,n,t); B[-1,:]=0; B[-1,-1]=1
    C=np.zeros((n,3)); C[0]=pts[0]; C[-1]=pts[-1]
    if n>2:
        A=B[:,1:-1]; rhs=pts-np.outer(B[:,0],pts[0])-np.outer(B[:,-1],pts[-1])
        lam=1e-6; R=np.diff(np.eye(n-2),2,axis=0) if n>4 else np.zeros((0,n-2))
        M=np.vstack([A,np.sqrt(lam)*R]); C[1:-1]=np.linalg.lstsq(M,np.vstack([rhs,np.zeros((len(R),3))]),rcond=None)[0]
    err=np.linalg.norm(B@C-pts,axis=1).max()
    return C,k,err

def coons(X,verts=None,n=8):
    """4-sided patch whose boundary interpolates loop X. returns ctrl (n,n,3), knots, edge err"""
    m=len(X); Xc=np.vstack([X,X[:1]])
    d=np.r_[0,np.cumsum(np.linalg.norm(np.diff(Xc,axis=0),axis=1))]; L=d[-1]
    # 4 sides by arclength, corners snapped to nearest points
    cid=[int(np.argmin(np.abs(d-L*q))) for q in (0,0.25,0.5,0.75)]
    cid.append(m)
    sides=[Xc[cid[i]:cid[i+1]+1] for i in range(4)]
    if min(len(s) for s in sides)<2: return None
    # densify sides with few points (linear)
    def dens(s):
        if len(s)>=6: return s
        t=np.linspace(0,1,8); idx=np.linspace(0,len(s)-1,len(s))
        return np.column_stack([np.interp(t*(len(s)-1),idx,s[:,k]) for k in range(3)])
    C=[];E=0
    for s in sides:
        c,k,e=fit_curve(dens(s),n); C.append(c); E=max(E,e)
    S1=C[0]; S2=C[1]; S3=C[2][::-1]; S4=C[3][::-1]   # bottom(u), right(v), top(u), left(v)
    P=np.zeros((n,n,3))
    P[:,0]=S1; P[-1,:]=S2; P[:,-1]=S3; P[0,:]=S4
    s=np.linspace(0,1,n)
    for i in range(1,n-1):
        for j in range(1,n-1):
            u,v=s[i],s[j]
            P[i,j]=(1-v)*S1[i]+v*S3[i]+(1-u)*S4[j]+u*S2[j]-((1-u)*(1-v)*P[0,0]+u*(1-v)*P[-1,0]+(1-u)*v*P[0,-1]+u*v*P[-1,-1])
    return P,clamped_knots(n),E
