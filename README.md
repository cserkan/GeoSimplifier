# GeoSimplifier

SOLIDWORKS içinde çalışan bir **shrink wrap** eklentisi. Aktif parçayı ya da montajı dışarıdan sarar, içini doldurur ve
**tek bir katı gövde** olarak yeni bir parçaya koyar. Voksel ya da mesh kullanılmaz: tüm işlemler SOLIDWORKS'ün
kendi B-rep geometrisi üzerinde yapılır, bu yüzden kalan yüzeyler orijinal yüzeylerin birebir aynısıdır.

## Nasıl çalışır

**Shrink Wrap** komutu (CommandManager'daki **GeoSimplifier** sekmesi veya Araçlar menüsü) sol tarafta bir
PropertyManager sayfası açar. Yeşil tik ile çalışır ve sonucu yeni bir parçaya ekler.

| Ayar | Anlamı |
| --- | --- |
| Delikleri ve boşlukları kapat | Açıkken aşağıdaki genişlik kullanılır; kapalıyken yalnızca birleştirme ve iç dolgu yapılır. |
| Kapatılacak en büyük açıklık | Sarma bu genişlikten dar açıklıkların içine girmez: ağzı bu genişlikten dar delikler, kanallar ve cepler yüzeyle aynı hizada kapatılır, birbirine bu mesafeden yakın parçalar birleştirilir. Daha geniş açıklıklar olduğu gibi kalır. |
| İç boşlukları doldur | Dışarıdan görünmeyen kapalı iç boşluklar doldurulur. |

Adımlar:

1. **Gövdeleri toplama**: Tüm görünür katı gövdelerin kopyaları alınır; montaj bileşenleri montaj koordinatlarına taşınır.
2. **Birleştirme**: Birbirine değen ya da iç içe geçen gövdeler boolean birleştirme ile tek gövde olur.
3. **Parçalar arası boşluklar**: Ayrı kalan gövdeler açıklık genişliğinin yarısı kadar dışa ofsetlenir, birleştirilir ve
   geri ofsetlenir. Bu sonuçtan yalnızca iki ya da daha fazla gövdeye değen köprüler alınır; böylece gövdelerin kendi
   iç köşeleri yuvarlanmaz.
4. **Delik kapatma**: Bir yüzün iç döngüsüyle sınırlanan bağlı yüzler bir delik, kanal ya da cep olarak bulunur
   (havşa ve yuva dahil). En dar ağzı verilen genişlikten küçük olan ve malzemenin içine doğru giden açıklıkların yüzleri
   silinir ve çevredeki yüzler büyütülerek kapatılır (Yüzü Sil > Sil ve Yama gibi). Kabartmalara dokunulmaz.
5. **İç dolgu**: Gövde, etrafındaki bir kutudan çıkarılır; kutunun kenarına değmeyen her parça bir iç boşluktur ve gövdeye eklenir.

Sınırlamalar: Yalnızca düzlemsel yüzlerdeki delikler ölçülür (örneğin bir milin silindirik yüzündeki radyal delik kapatılmaz).
Parçalar arası köprüleme SOLIDWORKS'ün ofset işlemine dayanır; çok karmaşık montajlarda ofset başarısız olursa
gövdeler ayrı bırakılır ve sonuç mesajında belirtilir.

## Proje yapısı

| Klasör | İçerik |
| --- | --- |
| `src/GeoSimplifier.Core` | SOLIDWORKS'ten bağımsız geometri yardımcıları ve ayarlar (netstandard2.0) |
| `src/GeoSimplifier.SolidWorks` | SOLIDWORKS eklentisi: kayıt, komut, PropertyManager sayfası, gövde oluşturma (.NET Framework 4.8) |
| `tests/GeoSimplifier.Core.Tests` | Geometri yardımcılarının testleri (her platformda çalışır) |

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
