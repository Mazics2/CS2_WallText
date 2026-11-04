# CS2_WallText — Kullanma Kılavuzu ve Geliştirme Yol Haritası (Roadmap)

Counter-Strike 2 sunucuları için **SwiftlyS2 (.NET 10)** altyapısıyla geliştirilmiş, harita bazlı kalıcı 3D duvar/dünya yazısı (`point_worldtext`), dinamik duyuru panosu ve etkileşimli `[E]` (USE) / Ateş Etme tetikleyici eklentisi.

---

## Bölüm 1: Kullanma Kılavuzu

### 1. Kurulum ve Temel Yapılandırma

1. Derlenen `CS2_WallText` klasörünü sunucunuzun `game/csgo/addons/swiftlys2/plugins/` dizinine yerleştirin.
2. Eklenti ilk kez çalıştığında aşağıdaki yapılandırma ve veri klasörlerini otomatik oluşturur:
   - **Genel Ayarlar**: `configs/plugins/CS2_WallText/config.json`
   - **Harita Verileri**: `configs/plugins/CS2_WallText/WorldTexts/<harita_adi>.json`

> **İpucu:** Eklenti dilini Türkçe yapmak için `config.json` içindeki `"Language"` değerini `"tr"` olarak ayarlayın. Varsayılan yönetici yetkisi `"walltext.admin"` olarak tanımlıdır.

```json
{
  "Enabled": true,
  "Language": "tr",
  "ChatPrefix": "[lime]WallText[default]",
  "CommandPermission": "walltext.admin",
  "DefaultFontSize": 40,
  "DefaultColor": "#FFFFFF",
  "DefaultWorldUnitsPerPx": 0.25,
  "DefaultJustifyHorizontal": 1,
  "DefaultJustifyVertical": 1,
  "DefaultFontName": ""
}
```

---

### 2. Komutlar ve Sohbet Girişi Kontrolleri

| Komut / Giriş | Açıklama |
| :--- | :--- |
| `!text` | Duvar yazısı yönetim menüsünü açar. |
| `!text reload` | Aktif haritanın JSON dosyasını diskten yeniden yükler ve tüm yazıları yeniler. |
| `cancel` veya `iptal` | Sohbetten metin, isim veya renk kodu beklenirken işlemi iptal eder. |

> **Not:** Sohbet girişi beklenirken (`yazı girme`, `isim değiştirme`, `renk kodu girme` vb.) açık olan menü otomatik kapanır. İşlemi iptal etmek için sohbete `iptal` yazabilir veya doğrudan `!text` komutunu tekrar kullanabilirsiniz.

---

### 3. Adım Adım Kullanım Senaryoları

#### 3.1. Yeni Duvar Yazısı Oluşturma ve Çoklu Satır Kullanımı
1. Oyun içinde `!text` yazarak ana menüyü açın.
2. **Yeni Duvar Yazısı Oluştur** seçeneğine tıklayın.
3. Yazıyı yerleştirmek istediğiniz duvara, zemine veya tavana nişan alıp **Sol Tık (`Mouse1`)** yapın.
   - Eklenti yüzeyin açısını (`HitNormal`) otomatik hesaplar ve yazıyı yüzeye dik ve düz şekilde hizalar.
4. Sohbete görünmesini istediğiniz metni yazıp `Enter`'a basın.
   - **Alt alta çoklu satır** yazmak için satır aralarına `|` koyun.
   - **Örnek**: `SUNUCUMUZA HOŞ GELDİNİZ! | Aktif Oyuncu: {PLAYERS}/{MAXPLAYERS} | Saat: {TIME}`

#### 3.2. Konumlandırma ve Duvara Göre Kaydırma
Bir yazıyı seçtikten sonra konumunu 3 farklı yöntemle değiştirebilirsiniz:
- **Nişangaha Taşı (Anlık)**: Menüdeki seçeneğe bastığınız anda baktığınız noktaya yazıyı taşır ve yüzey açısına hizalar.
- **Konumu Değiştir (Sol Tık Yakala)**: Seçeneğe bastıktan sonra istediğiniz yere gidip `Mouse1` ile tıkladığınız noktaya taşır.
- **Hassas Konum Ayarı (Fine-Tune)**:
  - **Adım Boyutu**: Her tıklamada kaç birim kayacağını belirler (`1 → 5 → 15 → 45 → 90`).
  - **Duvar Boyunca Sağa / Sola Kaydır**: Açılı duvarlarda `X/Y` ekseni hesabı yapmadan yazıyı duvar yüzeyi boyunca sağa veya sola kaydırır.
  - **Duvardan Dışarı Çek / İçeri İt**: Yazı duvarın içine gömüldüyse dışarı çeker veya duvara yaklaştırır.
  - **Dünya Eksenleri (`±X`, `±Y`, `±Z`)**: Klasik koordinat eksenlerinde yukarı/aşağı veya ileri/geri taşır.

#### 3.3. Yönelim, Döndürme ve Billboard Modları
Yazı detay menüsünden **Yönelim & Döndürme** alt menüsüne girerek:
- **90° Sağa / Sola Döndür**: Yazıyı yatay eksende hızlıca diğer duvar yönüne çevirir.
- **Dik / Yatay Yatır**: Duvar yazısını zemine/tavana yatırır veya tekrar dikleştirir.
- **Yazıyı Baş Aşağı Çevir (180°)**: Ters duran yazıları düzeltir.
- **Yönelim Modu (`ReorientMode`)**:
  - `Sabit (Duvara Yapışık)`: Yazı yerleştirdiğiniz yüzey açısında sabit durur.
  - `Oyuncuya Dön (Tam Billboard)`: Oyuncu nereden bakarsa baksın yazı tam olarak oyuncunun kamerasına döner.
  - `Oyuncuya Dön (Yatay)`: Yazı dikliğini koruyarak sadece sağa-sola oyuncuya doğru döner.

#### 3.4. Görünüm, Renk, Font ve Hizalama
- **Renk & Gökkuşağı**: **Renk Seç** menüsünden **Gökkuşağı Modu (RGB)** açılabilir, 11 hazır renkten biri seçilebilir veya **Özel Renk Kodu Gir** ile sohbetten `#FF5500` ya da `255,85,0` formatında renk verilebilir.
- **Yazı Boyutu (`FontSize`) & Kalınlık (`WorldUnitsPerPx`)**:
  - `Yazı Boyutu` çözünürlüğü ve boyutu büyütür/küçültür (`5` – `500`).
  - `Harf Kalınlığı / Ölçek` yazının dünyadaki fiziksel büyüklüğünü çarpan olarak ayarlar (`0.05` – `5.0`).
- **Font Seçimi**: `Varsayılan`, `Arial`, `Impact`, `Trebuchet MS`, `Verdana`, `Consolas`, `Tahoma` arasında geçiş yapılabilir.
- **Yatay & Dikey Hizalama**: Yatayda `Sol / Orta / Sağ`, dikeyde `Üst / Orta / Alt` hizalama seçilebilir.
- **Duvar Arkası Görünürlük (`DepthOffset`)**: Açıldığında yazı ince engellerin arkasından veya yüzey çakışmalarından (Z-fighting) etkilenmeden net görünür.

---

### 4. Dinamik Değişkenler (Canlı Veriler)

Normal yazılarda, dönüşümlü panolarda ve etkileşim mesajlarında aşağıdaki değişkenler **her saniye gerçek zamanlı** güncellenir (büyük/küçük harf duyarsızdır):

| Değişken | Açıklama | Örnek Çıktı |
| :--- | :--- | :--- |
| `{TIME}` | Sunucu saati (`HH:mm:ss`) | `21:45:10` |
| `{DATE}` | Sunucu tarihi (`dd.MM.yyyy`) | `04.11.2026` |
| `{DAY}` / `{MONTH}` / `{YEAR}` | Gün / Ay / Yıl | `04` / `11` / `2026` |
| `{MAP}` | Aktif harita adı | `de_dust2` |
| `{SERVERNAME}` / `{HOSTNAME}` | Sunucu adı (`hostname` ConVar) | `TR Pro Public #1` |
| `{PLAYERS}` | Aktif gerçek oyuncu sayısı | `18` |
| `{BOTS}` | Aktif bot sayısı | `2` |
| `{MAXPLAYERS}` | Sunucu kapasitesi | `32` |
| `{CT_PLAYERS}` / `{T_PLAYERS}` | CT ve T takımlarındaki oyuncu sayısı | `9` / `9` |
| `{SPEC_PLAYERS}` | İzleyici (`SPEC`) sayısı | `0` |
| `{ALIVE}` | Hayatta olan toplam oyuncu sayısı | `14` |
| `{NAME}` / `{PLAYER}` | *(Sadece Etkileşimde)* Tetikleyen oyuncunun adı | `Devrim` |
| `{STEAMID64}` / `{STEAMID}` | *(Sadece Etkileşimde)* Oyuncunun 64-bit SteamID'si | `76561198000000000` |
| `{SLOT}` / `{USERID}` | *(Sadece Etkileşimde)* Oyuncunun slot numarası | `3` |

---

### 5. Dönüşümlü Duyuru Panosu (Rotating Announcer)

Tek bir duvar yazısı üzerinde birden fazla duyurunun sırayla değişmesini sağlar:
1. Yazı detay menüsünden **Dönüşümlü Yazı (Rotating)** menüsüne girin.
2. **Dönüşümlü Mod: AÇIK** yapın.
3. **Yeni Mesaj Ekle** seçeneğiyle sırayla gösterilecek mesajları ekleyin (her mesajda `|` ile alt satır ve `{PLAYERS}`, `{TIME}` gibi değişkenler kullanabilirsiniz).
4. **Geçiş Süresini Değiştir** ile mesajların kaç saniyede bir değişeceğini ayarlayın (`3sn → 5sn → 10sn → 15sn → 30sn → 60sn`).

---

### 6. Etkileşimli Butonlar (`[E]` Tuşu ve Ateş Etme Tetikleyicileri)

Duvar yazılarını oyuncuların basabileceği interaktif butonlara dönüştürebilirsiniz:
1. Yazı detay menüsünden **Etkileşim & Tetikleyici ([E]/Shoot)** menüsüne girin.
2. **Tetikleme Modu** seçin:
   - `Yaklaşıp [E] Basınca`: Oyuncu yazıya yaklaşıp nişan alarak `E` (`+use`) tuşuna bastığında çalışır.
   - `Silahla Ateş Edince`: Oyuncu mesafe dahilinde yazıya ateş ettiğinde çalışır.
3. **Sohbet Mesajı Ayarla**: Tetiklendiğinde oyuncuya gönderilecek mesajı girin.
   - Renk etiketleri desteklenir: `[lime]`, `[red]`, `[gold]`, `[lightblue]`, `[default]` vb.
   - Örnek: `[lime]Merhaba {NAME}! [default]Discord adresimiz: [gold]discord.gg/sunucu`
4. **Konsol Komutu Ayarla**: Tetiklendiğinde çalıştırılacak komutu girin.
   - `;` ile birden fazla komut ayırabilirsiniz.
   - Komutun başında `{PLAYER}` varsa veya `!komut` / `/komut` şeklinde yazılırsa **oyuncu adına**, aksi halde **sunucu konsolunda** çalıştırılır.
   - **Örnek 1 (Oyuncu Sohbet Komutu)**: `!market` (Oyuncu sohbete `!market` yazmış gibi market menüsünü açar)
   - **Örnek 2 (Sunucu Komutu)**: `css_givecredits {STEAMID64} 100`
5. **Tetikleme Mesafesi** (`100` – `1000` birim) ve **Bekleme Süresi / Cooldown** (`0.5sn` – `30sn`) ayarlarını ihtiyacınıza göre düzenleyin.

---

## Bölüm 2: Geliştirme Yol Haritası (Roadmap)

### Faz 1 — Çekirdek Stabilizasyon ve Gelişmiş Konumlandırma (Tamamlandı — v1.1)
- [x] Doğrulanmış `EyePosition` ve `TraceParams.Builder` ile hatasız Aim Trace sistemi.
- [x] Harita geçişlerinde `GlobalVars.CurrentTime` sıfırlanma koruması ve aynı harita reload desteği.
- [x] Slot tabanlı birleşik sohbet istemi (`_pendingChatBySlot`) ve disconnect temizliği.
- [x] Duvara göre kaydırma (`Slide Right/Left`, `Pull Out/Push In`) ve ayrıştırılmış **Yönelim & Döndürme** alt menüsü.
- [x] Font ailesi seçimi (`FontName`), Dikey hizalama (`JustifyVertical`) ve 500-byte güvenli UTF-8 kesme.
- [x] Dönüşümlü panolarda canlı değişken güncellemesi ve `[E]` etkileşimlerinde en yakın hedef seçimi + Cooldown ayarı.

### Faz 2 — Görsel Önizleme, Şablonlar ve Çok Renkli Panolar (Hedef: v1.2)
- [ ] **Canlı Hayalet Önizleme (Live Ghost Preview)**: Admin yeni yazı oluştururken veya taşırken `Mouse1`'e basmadan önce baktığı yüzeyde geçici bir önizleme yazısının nişangahla birlikte kayması.
- [ ] **Hazır Şablonlar (Preset Templates)**: `config.json` içinde tanımlanan hazır şablonların (*Kurallar Panosu*, *VIP Bilgi*, *Discord Butonu*) menüden tek tıkla duvara yapıştırılması.
- [ ] **Çok Renkli Satır Grupları (Composite Multi-Color Lines)**: Bir panonun her satırına farklı renk ve font boyutu verebilen bağlı alt-entity (`Parent-Child WorldText Group`) sistemi.

### Faz 3 — İleri Etkileşim, Yetkilendirme ve Zamanlama (Hedef: v1.3)
- [ ] **Yetki ve Takım Kısıtlamalı Butonlar**: `[E]` / Ateş etme butonlarına `RequiredPermission` (ör. `vip.button`) ve `AllowedTeam` (`CT`, `T`, `All`) eklenmesi.
- [ ] **Seçilebilir Ses Efektleri (SoundEvent)**: Etkileşim menüsünden tetikleme sesini değiştirebilme veya kapatabilme.
- [ ] **Koşullu / Süreli Duvar Yazıları**: Sadece `Warmup` süresince, sadece round başında veya belirli bir bitiş tarihine kadar aktif kalan etkinlik yazıları.

### Faz 4 — Canlı Veritabanı Panoları ve Ağ Optimizasyonu (Hedef: v2.0)
- [ ] **Canlı Liderlik Tablosu (Database Leaderboard)**: MySQL / SQLite bağlantısı ile `{TOP_KILLS_5}`, `{TOP_CREDITS_5}`, `{TOP_PLAYTIME_5}` sıralama panoları.
- [ ] **Görünürlük Mesafesi (Distance Culling)**: Yazı başına `MaxVisibleDistance` tanımlanarak uzaktaki yazıları gizleme.
- [ ] **Haritalar Arası Dışa/İçe Aktarım (Prefab Export)**: Bir haritada tasarlanan duyuru panosu stilinin başka bir haritaya kopyalanabilmesi.
