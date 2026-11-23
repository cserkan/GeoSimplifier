import numpy as np,pickle,sys,matplotlib; matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
ang=int(sys.argv[1])
L=pickle.load(open('hibrit/edges_%d.pkl'%ang,'rb'))
views={'on':(np.array([1,0,0]),np.array([0,0,1])),'izo':(np.array([0.707,0.707,0]),np.array([-0.408,0.408,0.816]))}
# determine axes: use world X right, Z up for front (camera looking -Y)
for name,(u,v) in views.items():
    segs=[np.column_stack([X@u,X@v]) for X in L]
    fig=plt.figure(figsize=(12,12),dpi=110); ax=fig.add_axes([0,0,1,1]); ax.set_aspect('equal'); ax.axis('off')
    ax.add_collection(LineCollection(segs,linewidths=0.25,colors='k')); ax.autoscale()
    fig.savefig('hibrit/kenar_%d_%s.png'%(ang,name)); plt.close(fig)
