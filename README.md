# GeoSimplifier

SOLIDWORKS içinde çalışan bir **shrink wrap** eklentisi. Aktif parçayı ya da montajı dışarıdan sarar ve
iç detaylardan, boşluklardan ve küçük açıklıklardan arındırılmış **tek bir katı gövde** olarak yeni bir parçaya koyar.

## Nasıl çalışır

**Shrink Wrap** komutu (CommandManager'daki **GeoSimplifier** sekmesi veya Araçlar menüsü) sol tarafta bir
PropertyManager sayfası açar. Yeşil tik ile çalışır ve sonucu yeni bir parçaya tek gövde olarak ekler.

| Ayar | Anlamı |
| --- | --- |
| Orijinal yüzeyleri koru (hassas) | Açıkken dış yüzeyler birebir korunur; yalnızca kapatılan delikler ve gövdeler arası boşluklar eklenir. Kapalıyken tüm gövde vokselden (basamaklı) oluşturulur. |
| Delikleri ve boşlukları kapat | Açıkken en dar yeri verilen genişlikten küçük delikler, kanallar ve gövdeler arası boşluklar tamamen kapatılır. |
| Çözünürlük | En uzun kenardaki voksel sayısı (16-2048). Delik tespitinin ve voksel modunda tüm yüzeyin hassasiyetini belirler. |

Adımlar:

1. **Geometri toplama**: Tüm görünür katı gövdeler üçgenlere ayrılır; montaj bileşenleri montaj koordinatlarına taşınır.
2. **Voksel yüzey ve dış dolgu**: Üçgenler bir voksel ızgarasına dökülür, dışarıdan flood fill yapılır; iç boşluklar kendiliğinden dolar.
3. **Delik kapatma**: Her eksene dik dilimlerde malzemeyle çevrili boş bölgeler bulunur, komşu dilimlerdekiler aynı deliğe bağlanır.
   En dar kesiti (boğazı) verilen genişlikten küçük olan delik, havşası ve yuvası dahil **tamamen** kapatılır.
   Tıpa, delik ağzındaki yüzeylerin gerçek yüksekliğinde biter, yani yüzeyle aynı hizadadır.
4. **Gövdeler arası boşluk**: Model birden fazla ayrı parçadan oluşuyorsa, verilen genişlikten yakın parçalar morfolojik kapama ile birleştirilir.
   Tek gövdeli parçalarda bu adım atlanır, çünkü iç köşeleri de doldururdu.
5. **Gövde oluşturma**:
   - *Hassas mod*: Orijinal gövdelerin kopyaları yalnızca tıpa ve köprü kutularıyla birleştirilir; dış yüzeyler birebir kalır ve az sayıda boolean işlemi gerekir.
   - *Voksel modu*: Katı hücreler kutulara birleştirilir ve kutular hiyerarşik olarak (böl ve birleştir) tek gövdeye çevrilir. Eğri yüzeyler basamaklı olur.

## Proje yapısı

| Klasör | İçerik |
| --- | --- |
| `src/GeoSimplifier.Core` | SOLIDWORKS'ten bağımsız shrink wrap algoritması (netstandard2.0) |
| `src/GeoSimplifier.SolidWorks` | SOLIDWORKS eklentisi: kayıt, komut, PropertyManager sayfası, gövde oluşturma (.NET Framework 4.8) |
| `tests/GeoSimplifier.Core.Tests` | Çekirdek algoritma testleri (her platformda çalışır) |

## Derleme ve kurulum (Windows)

1. SOLIDWORKS kurulu olmalı. API DLL'leri varsayılan olarak
   `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist` altında aranır. Farklı bir yerdeyse:
   `dotnet build -p:SolidWorksApiDir="D:\...\api\redist"`
2. `dotnet build GeoSimplifier.sln`
3. Yönetici olarak açılmış komut isteminde: `scripts\register.cmd`
4. SOLIDWORKS'te **Araçlar > Eklentiler** altında **GeoSimplifier**'ı etkinleştirin.
5. Bir parça ya da montaj açın, CommandManager'daki **GeoSimplifier** sekmesinden **Shrink Wrap** komutunu çalıştırın.

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
