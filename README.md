# GeoSimplifier

SOLIDWORKS içinde çalışan bir **shrink wrap** eklentisi. Aktif parçayı ya da montajı dışarıdan sarar ve
iç detaylardan, boşluklardan ve küçük açıklıklardan arındırılmış **tek bir katı gövde** olarak yeni bir parçaya koyar.

## Nasıl çalışır

**Shrink Wrap** komutu (CommandManager'daki **GeoSimplifier** sekmesi veya Araçlar menüsü) sol tarafta bir
PropertyManager sayfası açar. Yeşil tik ile çalışır ve sonucu yeni bir parçaya tek gövde olarak ekler.

| Ayar | Anlamı |
| --- | --- |
| Orijinal yüzeyleri koru (hassas) | Açıkken dış yüzeyler birebir korunur; voksel yalnızca iç boşlukları, kapatılan delikleri ve gövdeler arası boşlukları doldurur. Hızlıdır. Kapalıyken tüm gövde vokselden (basamaklı) oluşturulur. |
| Delikleri ve boşlukları kapat | Açıkken verilen genişlikten dar delikler, kanallar ve gövdeler arası boşluklar kapatılır. |
| Çözünürlük | En uzun kenardaki voksel sayısı (16-2048). Kapatılan deliklerin tıpasının ve voksel modunda tüm yüzeyin hassasiyetini belirler. |

Adımlar:

1. **Geometri toplama**: Tüm görünür katı gövdeler üçgenlere ayrılır; montaj bileşenleri montaj koordinatlarına taşınır.
2. **Voksel yüzey**: Üçgenler bir voksel ızgarasına dökülür.
3. **Boşluk kapama**: Yüzey, kapatılacak genişliğin yarısı kadar genişletilir, dışarıdan flood fill yapılır ve aynı miktar geri daraltılır (morfolojik kapama). Dışarıdan ulaşılamayan her şey katıdır.
4. **Gövde oluşturma**:
   - *Hassas mod*: Orijinal gövdelerin kopyaları, dış yüzeye değmeyen dolgu hücreleriyle birleştirilir. Kapatılan deliklerin ağzında en fazla 1-2 voksel derinliğinde çukur kalabilir.
   - *Voksel modu*: Katı hücreler kutulara birleştirilir ve kutular hiyerarşik olarak (böl ve birleştir) tek gövdeye çevrilir.

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
