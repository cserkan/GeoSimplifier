def write_iges(path, polylines, scale=1000.0, color=None):
    """polylines: list of (n,3) arrays in metres -> IGES 106 form 12, mm units"""
    def gfield(s): return '%dH%s'%(len(s),s)
    G=[ '1H,','1H;',gfield('3DWrap'),gfield(path.split('/')[-1]),gfield('3DWrap hibrit'),gfield('3DWrap'),
        '32','38','6','308','15',gfield('3DWrap'),'1.0','2',gfield('MM'),'1','0.01',gfield('20261124.000000'),
        '0.0001','10000.0',gfield('Claude'),gfield('3DWrap'),'11','0',gfield('20261124.000000')]
    gtxt=','.join(G)+';'
    Gl=[gtxt[i:i+72] for i in range(0,len(gtxt),72)]
    D=[];Pl=[]
    pline=1
    for k,X in enumerate(polylines):
        X=X*scale
        vals=['106','2',str(len(X))]
        for p in X: vals+=['%.4f'%p[0],'%.4f'%p[1],'%.4f'%p[2]]
        de=2*k+1
        txt=','.join(vals)+';'
        # split into 64-char records
        recs=[];cur=''
        for tok in txt.replace(';',',;').split(','):
            if tok==';': 
                cur=cur[:-1]+';' if cur.endswith(',') else cur+';'; continue
            piece=tok+','
            if len(cur)+len(piece)>64: recs.append(cur); cur=''
            cur+=piece
        if cur: recs.append(cur)
        n=len(recs)
        D.append('%8d%8d%8d%8d%8d%8d%8d%8d%8sD%7d'%(106,pline,0,1,0,0,0,0,'00000000',de))
        D.append('%8d%8d%8d%8d%8d%8s%8s%8s%8dD%7d'%(106,0,0 if color is None else color,n,12,'','','',0,de+1))
        for r in recs:
            Pl.append('%-64s%8dP%7d'%(r,de,pline)); pline+=1
    S=['3DWrap hibrit kenar egrileri'.ljust(72)+'S%7d'%1]
    with open(path,'w',newline='\r\n') as f:
        for l in S: f.write(l+'\n')
        for i,l in enumerate(Gl): f.write(l.ljust(72)+'G%7d\n'%(i+1))
        for l in D: f.write(l+'\n')
        for l in Pl: f.write(l+'\n')
        f.write(('S%7dG%7dD%7dP%7d'%(len(S),len(Gl),len(D),len(Pl))).ljust(72)+'T%7d\n'%1)
