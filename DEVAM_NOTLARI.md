# Devam notları (10 Ekim 2026)

Bu dosya, 10 Ekim 2026 oturumunda kalınan yeri ve bekleyen önerileri tutar. Yeni bir sohbete buradan devam edilir.

## Bugün tamamlananlar (yayında: `publish\current`, 11:25)

- Ücretsiz yapay zekâ yedek zinciri: Cloudflare, NVIDIA, Groq, OpenRouter, Gemini (`Services\FreeLlmProviders.cs`).
  ERP sohbeti ve Watcher (Telegram, Copilot yedeği, ajan ekibi) kullanıyor.
- Ücretsiz yerel ses: Piper, Türkçe sesler fahrettin, dfki ve fettah (`Services\PiperSpeechService.cs`).
  Jarvis seslendirme sırası: Piper, ElevenLabs, Windows sesi.
- Web araması: SerpAPI öncelikli. Kota biterse veya hata verirse ücretsiz Bing/DuckDuckGo ve Jina Reader'a otomatik geçiliyor.
- Köprüde çift işleme koruması (`bridge.js`, `ide-watch.ps1`).
- Kök klasör ve gereksiz klasör temizliği. Başlatıcı yalnızca `publish\current\ArsanGazERP.exe` dosyasını açıyor.
- Önceki sürümün yedeği: `publish\backup-20261010-112548`.
- Commit yapılmadı.

## Kullanıcının yapması gereken

- ERP'de "NVIDIA NIM ve Ücretsiz Yedek Sağlayıcılar" penceresine Groq, OpenRouter ve Gemini ücretsiz anahtarlarını girip
  "Kaydet ve Test Et" ile kaydetmek.
- Gemini'nin ücretsiz katmanı gönderilen içeriği ürün geliştirmede kullanabilir. Bu yüzden sırada en sonda.

## Doğrulanmayanlar (sıradaki kontroller)

- Groq, OpenRouter ve Gemini'ye gerçek çağrı: anahtar girildikten sonra test edilecek.
- Piper'ın ERP sohbet penceresi içinden konuşması.
- SerpAPI kotası bitince ücretsiz aramaya gerçekten geçilmesi: kod hazır, gerçek kota bitişi yaşanmadı.
- Ücretsiz Bing aramasının sonuç kalitesi zayıf olabilir; bir denemede ilgisiz ilk sonuç geldi.

## Bekleyen ERP geliştirme önerileri (henüz başlanmadı)

Önerilen sıra: önce 1, 2 ve 3; sonra 4. e-Fatura ve rota ayrı karar gerektiriyor.

1. Tüp test tarihi uyarısı (kolay): testi 30 gün içinde dolacak tüpler sabah özetine ve Telegram'a gelsin.
2. Barkodla hızlı tüp hareketi (kolay-orta): tüp ekranına "Okut" kutusu (USB okuyucu klavye gibi çalışır).
   Okut, müşteri seç, Teslim/İade/Dolum seç; hareket tek adımda kaydedilsin. Barkod etiketi yazdırma da eklenebilir.
3. Depozito ve müşteride kalan tüp defteri (orta): müşteri bazında içerideki tüp sayısı ve depozito.
   Uzun süredir dönmeyen tüpler raporu ve Telegram hatırlatması.
4. Telegram'dan onaylı kayıt (orta): ör. "Ahmet Ticaret'e 5 tüp teslim" yazılınca Evet/Hayır onayıyla kayıt yapılsın.
   Her işlem günlüğe yazılsın. İlk kapsam: tüp hareketi ve tahsilat.
5. e-Fatura / e-Arşiv (büyük): faturalarda önce satır (kalem) yapısı gerekiyor.
   Özel entegratör firma ve API anahtarı gerekiyor; hangi firmayla çalışıldığı öğrenilmeli.
6. Yeniden sipariş tahmini (orta): "genelde 14 günde bir alıyor, 17 gün oldu" gibi arama listesi.
   Yeterli hareket verisi gerekiyor.
7. Teslimat rotası (büyük, sonraya): ilk adım olarak günün teslimat listesi ve Google Haritalar rota bağlantısı Telegram'a gelsin.

Not: gerçek veritabanındaki tüp ve hareket sayılarına bakılamadı; barkod ve hareket özelliklerinin şu an ne kadar
kullanıldığı bilinmiyor.

## Karar: sistemde fatura olmayacak (10 Ekim 2026)

e-Fatura, fatura PDF'i ve fatura/fiş okuma (OCR) önerileri iptal. Yalnızca "Geciken faturalar" sekmesi duruyor;
kaldırılıp kaldırılmayacağı kullanıcıya soruldu.

## 10 Ekim akşamı kodlananlar (derlendi, testler geçti, henüz yayınlanmadı)

- Barkodla hızlı tüp hareketi: Operasyon merkezi, Tüp envanteri sekmesi. Okut + işlem + müşteri, Enter ile kayıt.
  Hareket türü (Müşteriye teslim, Depoya iade, ...) kaydediliyor; iadede hangi müşteriden döndüğü artık saklanıyor.
- QR + Code128 tüp etiketi: "Etiket bas" (CodeGlyphX 3.0.0, Apache-2.0). Yazdır ve PNG kaydet.
- Sipariş tahmini sekmesi: müşterinin teslimleri arasındaki ortanca gün sayısı (`Services\ReorderPrediction.cs`).
  ML.NET kullanılmadı: müşteri başına seri çok kısa, ortanca daha sağlam ve ek paket gerektirmiyor.
- MCP sunucusu `Tools\ErpMcpServer` (salt okunur, 10 araç: ERP tüp/müşteri + CRM firma/takip), `.cursor\mcp.json` ile kayıtlı.
  ERP veritabanında şu an müşteri/tüp/hareket kaydı yok; CRM'de 2521 firma var (hepsi "Yeni").
- Ertelenen: Velopack otomatik güncelleme (mevcut yayın/güncelleme düzenini değiştirir, ayrı karar).

## Telefon uygulaması (APK) ve mobil sunucu (10 Ekim 2026 akşam)

- `Tools\MobileApi`: bilgisayardaki sunucu (yalnızca 127.0.0.1:5077), `publish\current\MobileApi`, HKCU Run ile otomatik başlar.
  Uzaktan erişim Cloudflare hızlı tüneli (`%LOCALAPPDATA%\ArsanGazERP\MobileApi\cloudflared.exe`); adres her yeniden
  başlatmada değişir ve Telegram'a otomatik gelir. `/indir` APK'yı verir. Kalıcı adres için ileride: alan adı ile adlandırılmış tünel veya Tailscale.
- Güvenlik: Telegram'a giden 6 haneli, 10 dakikalık, 5 denemelik eşleştirme kodu → cihaza 256 bit anahtar (sunucuda yalnızca SHA-256 özeti, `devices.json`).
  Cihaz silmek: `devices.json` içinden kaydı kaldır. Yerel test eşleştirmesi: `ArsanGazERP.MobileApi.exe --yerel-eslestir <ad> <dosya>`.
- `Tools\MobileApp` (MAUI, yalnız Android): Ajan sohbeti (Android Türkçe konuşma tanıma + sesli yanıt), Tüp (kamera ile barkod/QR, hareket kaydı),
  CRM arama, Takip listesi (ara/WhatsApp/not/takip tarihi), Özet. APK: `publish\mobile\ArsanGazERP-Mobil-1.0.apk` (debug anahtarıyla imzalı).
- Derleme: `dotnet build Tools\MobileApp\ArsanGazMobile.csproj -c Release -f net10.0-android -p:AndroidSdkDirectory=%LOCALAPPDATA%\Android\sdk -p:JavaSdkDirectory=%LOCALAPPDATA%\Android\jdk17`
- Telegram'dan 30 MB dosya gönderilemiyor (AgentWatcher yükleme süresi 60 sn); APK bu yüzden `/indir` bağlantısıyla dağıtılıyor.

## Öneriler: yeni yenilikler (10 Ekim 20:15, henüz başlanmadı; önerilen sıra 1, 2, 7, sonra 3, 4)

1. Telegram'dan günlük arama listesi ve tek tuşla sonuç kaydı (CRM'de 2521 firmanın hepsi "Yeni", hiç aktivite yok).
2. Otomatik gece yedeği (ERP + CRM) ve Telegram bildirimi; şu an yedek yalnızca elle OneDrive'a.
3. Tüp QR'ından müşterinin WhatsApp ile kendi siparişini vermesi (wa.me bağlantısı); ERP okutma kutusu bağlantıdan seri no'yu çözer.
4. Telegram sesli notu → yerel Whisper ile yazıya → onaylı CRM notu ve takip tarihi.
5. EPDK resmî LPG fiyat bülteni servisinden günlük fiyat değişimi uyarısı (apigateway.epdk.gov.tr/lpgBayiSatisFiyatBultenGunluk; denenmedi).
6. CRM haritası ve günlük ziyaret rotası (Google Haritalar bağlantısı). Prospect kayıtlarında koordinat saklanmıyor; önce eklenmeli.
7. CRM veri temizliği: aynı telefon/ad ile mükerrer firmalar, telefon biçimi düzeltme.

## Faydalı GitHub depoları araştırması (10 Ekim 2026 akşam)

Öncelikli:
- EvotecIT/CodeGlyphX: bağımlılıksız QR ve barkod üretme/okuma, WPF kontrolleri, .NET 10 desteği (v1.4.0, Nisan 2026).
  Öneri 2 (barkodla tüp hareketi) için. Zebra etiket yazıcısı varsa ek olarak BinaryKits/BinaryKits.Zpl.
- QuestPDF/QuestPDF: fatura, irsaliye ve rapor PDF'i. Yıllık cirosu 1 milyon USD altındaki işletmeye ücretsiz.
  ERP'de şu an PDF çıktısı yok.
- modelcontextprotocol/csharp-sdk v2.0 (Temmuz 2026): ERP veritabanını Cursor/Copilot ajanlarına salt okunur araçlar olarak açmak.
  Ajanlar tahmin yerine gerçek veriye bakar.
- ugur-murat-alt/efatura-dotnet ve hkutluay/UblTr: UBL-TR 1.2.1 XML üretimi. Entegratör bağlantıları taslak (stub);
  öneri 5 için ön hazırlık, entegratör firma yine gerekli.
Sonra:
- Microsoft.ML ForecastBySsa: yeniden sipariş tahmini (öneri 6), işlemcide çalışır, yeterli hareket verisi gerekir.
- Windows.Media.Ocr (Windows'ta hazır, ücretsiz) veya RapidOcrNet: Telegram'dan gelen fatura/fiş fotoğrafını okumak.
  Türkçe dil paketi ve masaüstü uygulamadan çağrılabilirlik denenmeli.
- velopack/velopack: otomatik güncelleme (ERP'yi elle kapatıp kopyalama yerine). Program giriş noktası değişir.
Şimdilik önerilmeyen:
- VROOM/OSRM rota optimizasyonu: sunucu/Docker gerekir; ilk adım için Google Haritalar bağlantısı yeterli.
- Litestream: Windows resmî olarak desteklenmiyor; ERP'de zaten OneDrive yedeği var (zamanlanması düşünülebilir).
- Telegram.Bot kütüphanesi: mevcut kendi Telegram kodumuz satır içi düğmeleri ve geri çağrıları zaten destekliyor.

## Ücretsiz ajan araştırmasından kalan notlar

- Önerilmeyenler: gpt4free, CLIProxyAPI (Google hesap engelleme riski), değiştirilmiş Claude Code dağıtımları.
- GitHub Models 30 Temmuz 2026'da kapandı.
- Bu donanımda (ekran kartı yok, 8 GB RAM, Docker yok) yerel büyük model, LiteLLM sunucusu veya SearXNG çalıştırmak uygun değil.
