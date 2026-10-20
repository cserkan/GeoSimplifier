# GeoSimplifier

SOLIDWORKS içinde çalışan bir **shrink wrap** eklentisi. Aktif parçayı ya da montajı dışarıdan sarar ve
iç detaylardan, boşluklardan ve küçük açıklıklardan arındırılmış **tek bir katı gövde** olarak yeni bir parçaya koyar.

## Nasıl çalışır

1. **Geometri toplama**: Parçadaki ya da montajdaki tüm görünür katı gövdeler üçgenlere ayrılır
   (`IFace2.GetTessTriangles`); montaj bileşenleri `IComponent2.Transform2` ile montaj koordinatlarına taşınır.
2. **Voksel yüzey**: Üçgenler, en uzun kenar boyunca `Çözünürlük` kadar hücreden oluşan bir ızgaraya dökülür.
3. **Boşluk kapama**: Yüzey `Boşluk kapama` yarıçapı kadar genişletilir, böylece dar açıklıklar ve
   birbirine yakın gövdeler arasındaki aralıklar kapanır.
4. **Dış dolgu**: Izgaranın kenarından içeri flood fill yapılır; dışarıdan ulaşılamayan her şey (iç boşluklar dahil) katıdır.
5. **Geri daraltma**: Katı aynı yarıçapla daraltılır (morfolojik kapama). Orijinal yüzey hücreleri her zaman korunur,
   yani sonuç modeli her zaman içine alır.
6. **Manifold düzeltme**: Yalnızca kenar ya da köşeden değen hücreler doldurulur, böylece geçerli bir katı oluşur.
7. **Gövde oluşturma**: Hücreler birleştirilmiş kutulara indirilir, `IModeler` ile birleştirilip
   (`IBody2.Operations2`) yeni bir parçaya `CreateFeatureFromBody3` ile eklenir.

Sonuç, orijinal yüzeyin üzerinde ya da en fazla yaklaşık bir voksel dışındadır. Çözünürlük arttıkça sargı sıkılaşır,
gövde oluşturma süresi uzar.

## Proje yapısı

| Klasör | İçerik |
| --- | --- |
| `src/GeoSimplifier.Core` | SOLIDWORKS'ten bağımsız shrink wrap algoritması (netstandard2.0) |
| `src/GeoSimplifier.SolidWorks` | SOLIDWORKS eklentisi: kayıt, komut, görev paneli, gövde oluşturma (.NET Framework 4.8) |
| `tests/GeoSimplifier.Core.Tests` | Çekirdek algoritma testleri (her platformda çalışır) |

## Derleme ve kurulum (Windows)

1. SOLIDWORKS kurulu olmalı. API DLL'leri varsayılan olarak
   `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist` altında aranır. Farklı bir yerdeyse:
   `dotnet build -p:SolidWorksApiDir="D:\...\api\redist"`
2. `dotnet build GeoSimplifier.sln`
3. Yönetici olarak açılmış komut isteminde: `scripts\register.cmd`
4. SOLIDWORKS'te **Araçlar > Eklentiler** altında **GeoSimplifier**'ı etkinleştirin.
5. Bir parça ya da montaj açın, görev panelindeki **GeoSimplifier** sekmesinden veya **Shrink Wrap** komutundan çalıştırın.

Kaldırmak için: `scripts\unregister.cmd`

### Sorun giderme

- **`RegAsm : error RA0000 ... bulunamıyor`**: Ya eklenti henüz derlenmemiştir (önce `dotnet build src\GeoSimplifier.SolidWorks`)
  ya da SOLIDWORKS API DLL'leri bulunamamıştır. `bin\Debug\net48` klasöründe `GeoSimplifier.SolidWorks.dll`,
  `GeoSimplifier.Core.dll` ve `SolidWorks.Interop.*.dll` dosyalarının olduğunu kontrol edin.
- **Derlemede `SolidWorks.Interop... bulunamadı` uyarısı**: `-p:SolidWorksApiDir=...` ile SOLIDWORKS'ün `api\redist` klasörünü verin.

## Testler

```
dotnet test tests/GeoSimplifier.Core.Tests
```
