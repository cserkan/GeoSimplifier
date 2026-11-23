# Hibrit hızlı model prototipi (6M31)

Hedef: DXF'te görünen, mate alan, hızlı açılan SLDPRT.

- vis_tri.py: 200 yönden ışın atarak dışarıdan görünen üçgenleri bulur (3,04 M → 1,64 M).
- decim2.py: görünen ağı gövde gövde sadeleştirir (pyfqmr, MIT). eps=1 → 480k üçgen, görüntü neredeyse aynı.
- edges.py + iges.py: görünen üçgenlerin keskin kenarlarını (dihedral > açı) ve açık kenarlarını polyline olarak çıkarır, IGES 106/12 (mm) yazar.
- veri/hibrit_veri.zip: 6M31_gorunur_eps1_480k_mm.stl, 6M31_gorunur_eps0.1_666k_mm.stl, 6M31_kenar_45.igs (26k çizgi, 139k parça), 6M31_kenar_60.igs (10k çizgi, 72k parça).
- veri/hibrit_1.6M.zip: tüm görünen üçgenler (mm).
- view_edges2.py: 6 standart görünüş (on/arka/sag/sol/ust/alt) için gizli çizgileri kaldırılmış kenarlar: keskin kenar + siluet, embree ile görünürlük. veri/gorunus_kenarlari.zip → 6M31_gorunus_<ad>.igs (mm). Sağ görünüş 86k polyline / 209k parça.
- planar.py: görünen ağda düzlemsel bölgeler: alanın %73'ü düzlemsel ama üçgenlerin yalnız %44'ü; eğri yüzeyler için silindir uydurma olmadan B-rep yüz sayısı çok yüksek kalır.
