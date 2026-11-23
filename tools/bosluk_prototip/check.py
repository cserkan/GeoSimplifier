import sys,time
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_SHELL
from OCP.BRepCheck import BRepCheck_Analyzer
from OCP.TopoDS import TopoDS
t=time.time()
r=STEPControl_Reader(); st=r.ReadFile(sys.argv[1]); print('read',st==IFSelect_RetDone,round(time.time()-t,1))
r.TransferRoots(); s=r.OneShape(); print('transfer',round(time.time()-t,1))
nf=0;bad=0;ex=TopExp_Explorer(s,TopAbs_FACE)
while ex.More():
    f=ex.Current(); nf+=1
    if not BRepCheck_Analyzer(f).IsValid(): bad+=1
    ex.Next()
print('faces',nf,'invalid',bad,round(time.time()-t,1))
