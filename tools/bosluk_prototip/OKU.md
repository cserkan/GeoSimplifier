# Grafik veride boşluk doldurma prototipi (Python)

GapFill aracının yazdığı `_grafik.stl` (tüm gövdelerin görüntü üçgenleri, öznitelik = gövde no) ve
`_donguler.csv` (döngü sınıfları) üzerinde çalışır:

1. `load.py`: STL'yi okur, gövde başına kaynak yapar, tek üçgende geçen kenarlardan açık döngüleri çıkarır.
2. `patch.py`: döngüleri CSV sınıflarıyla eşler; düzlemsel döngüyü düzlemde üçgenler (Delaunay + membran).
3. `clean.py`: yamaları düzgün yüzeye çevirir ve STEP AP214 yazar:
   düz delik/ağız → PLANE (+ uyuyorsa CIRCLE kenar), düz olmayan → 4 kenarlı Coons B-spline yüzey
   (kenarları döngüye ~0.1 mm oturur, yüzey sınırı = kenar eğrileri, geçerli B-rep).
4. `build.py`: hepsini çalıştırır → `3DWrap_6M31_yamalar.step`.
5. `check.py`: STEP'i OCCT (cadquery-ocp, yalnız doğrulama için) ile okuyup yüz geçerliliğini sayar.

Bağımlılık: numpy, scipy, pillow (render), cadquery-ocp (yalnız check.py).
