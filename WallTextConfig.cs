using System.Collections.Generic;

namespace CS2WallText;

public class WallTextConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Empty string = no permission restriction. Defaults to CS2_Admin's root node so existing admins already have access.</summary>
    public string Permission { get; set; } = "admin.root";

    public string DefaultFontName { get; set; } = "";
    public float DefaultFontSize { get; set; } = 24f;
    public float DefaultScale { get; set; } = 0.4f;
    public string DefaultColorHex { get; set; } = "#FFFFFF";
    public bool DefaultBackground { get; set; } = false;
    public bool DefaultFullbright { get; set; } = true;
    public bool DefaultFacePlayer { get; set; } = false;
    public int DefaultJustifyHorizontal { get; set; } = 1;
    public int DefaultJustifyVertical { get; set; } = 1;
    public float DefaultDepthOffset { get; set; } = 0.5f;

    /// <summary>Interval in seconds to update dynamic placeholders like {TIME}, {PLAYERS}. Set to 0 to disable dynamic updates.</summary>
    public float DynamicUpdateIntervalSeconds { get; set; } = 2.0f;

    /// <summary>Which key under <see cref="Messages"/> is used for all chat/menu text. Defaults to "en".</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// All translatable text, editable directly in WallText.json. Language code -> (message key -> text).
    /// Falls back to "en" for any key missing from the selected language.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Messages { get; set; } = DefaultMessages();

    public bool MergeMissingDefaultMessages()
    {
        var changed = false;
        foreach (var (lang, keys) in DefaultMessages())
        {
            if (!Messages.TryGetValue(lang, out var existing))
            {
                Messages[lang] = new Dictionary<string, string>(keys);
                changed = true;
                continue;
            }

            foreach (var (key, value) in keys)
            {
                if (!existing.ContainsKey(key))
                {
                    existing[key] = value;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static Dictionary<string, Dictionary<string, string>> DefaultMessages()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new()
            {
                ["chat.prefix"] = "[lime]WallText[default]",
                ["chat.no_permission"] = "You do not have permission to use this command.",
                ["chat.usage_general"] = "Usage: !text [add|edit|delete|list|tp|reload|help] <name>",
                ["chat.usage_add"] = "Usage: !text add <name>",
                ["chat.usage_edit_cmd"] = "Usage: !text edit <name>",
                ["chat.usage_delete"] = "Usage: !text delete <name>",
                ["chat.usage_tp"] = "Usage: !text tp <name>",
                ["chat.already_exists"] = "A world text named '{0}' already exists.",
                ["chat.not_found"] = "'{0}' was not found.",
                ["chat.capturing_point"] = "Aim at the wall where you want '{0}' and shoot (a knife works too).",
                ["chat.enter_text"] = "Now type the text you want displayed in chat (spaces allowed, '\\n' for newlines, type 'cancel' to abort).",
                ["chat.created"] = "'{0}' created with text: \"{1}\".",
                ["chat.text_changed"] = "Text updated to: \"{0}\".",
                ["chat.enter_new_name"] = "Type the new name in chat now (type 'cancel' to abort).",
                ["chat.renamed"] = "Renamed to '{0}'.",
                ["chat.rename_cancelled"] = "Cancelled.",
                ["chat.deleted"] = "'{0}' deleted.",
                ["chat.list_header"] = "World texts on {0}:",
                ["chat.list_empty"] = "No world texts saved for this map yet.",
                ["chat.console_only"] = "This command can only be used in-game.",
                ["chat.already_capturing"] = "You already have a point capture in progress.",
                ["chat.reaim_prompt"] = "Aim at the new wall position for '{0}' and shoot (or knife).",
                ["chat.reaim_success"] = "'{0}' repositioned and aligned successfully.",
                ["chat.teleported"] = "Teleported to '{0}'.",
                ["chat.duplicated"] = "'{0}' duplicated from '{1}'. Aim and shoot to place it.",
                ["chat.enter_color_hex"] = "Type the HEX color code in chat (e.g. #FF5500 or FF5500, type 'cancel' to abort).",
                ["chat.color_changed"] = "Color changed to {0}.",
                ["chat.invalid_color_hex"] = "Invalid HEX color format. Example: #FF0000 or #FFFFFF.",
                ["chat.reloaded"] = "Configuration and world texts reloaded for map '{0}' ({1} loaded).",
                ["chat.help_header"] = "--- [lime]WallText Commands[default] ---",
                ["chat.help_add"] = "!text add <name> - Create new text (aim & shoot)",
                ["chat.help_edit"] = "!text edit <name> - Rename text",
                ["chat.help_delete"] = "!text delete <name> - Delete text",
                ["chat.help_list"] = "!text list - List all texts on current map",
                ["chat.help_tp"] = "!text tp <name> - Teleport to text",
                ["chat.help_reload"] = "!text reload - Reload plugin config & texts",
                ["chat.help_menu"] = "!text - Open interactive management menu",

                // Orientation & alignment
                ["chat.moved_to_crosshair"] = "'{0}' moved and aligned upright at your crosshair.",
                ["menu.option.orientation_menu"] = "Orientation & Quick Align",
                ["menu.title.orientation"] = "Orientation: {0}",
                ["menu.option.pitch_up"] = "Pitch Up / Stand Up (+90 deg)",
                ["menu.option.pitch_down"] = "Pitch Down (-90 deg)",
                ["menu.option.roll_rotate"] = "Roll Sideways (+90 deg)",
                ["menu.option.preset_vertical"] = "Preset: Stand Up (Pitch 90 deg)",
                ["menu.option.preset_vertical_neg"] = "Preset: Stand Up (Pitch -90 deg)",
                ["menu.option.preset_wall_roll90"] = "Preset: Wall Upright (Roll 90 deg)",
                ["menu.option.preset_flat"] = "Preset: Flat / Default (Pitch 0, Roll 0)",
                ["menu.option.snap_upright"] = "Snap Upright (Pitch/Roll = 0)",
                ["menu.option.rotate_90"] = "Rotate 90 deg Clockwise",
                ["menu.option.flip_180"] = "Flip 180 deg (Reverse)",
                ["menu.option.face_admin"] = "Face Towards Me (Horizontal)",
                ["menu.option.face_admin_3d"] = "Face Towards My Eyes (3D)",
                ["menu.option.move_to_crosshair"] = "Move to My Crosshair (Instant)",

                // Rainbow
                ["menu.option.rainbow"] = "Rainbow (RGB): {0}",
                ["menu.option.rainbow_speed"] = "Rainbow Speed: {0}x",

                // Rotating announcer
                ["menu.option.rotating_menu"] = "Rotating Announcer ({0} Messages)",
                ["menu.title.rotating"] = "Rotating Board: {0}",
                ["menu.option.rotating_toggle"] = "Rotation Active",
                ["menu.option.rotating_interval"] = "Interval: {0}s",
                ["menu.option.add_rotating_msg"] = "Add Message (via chat)",
                ["menu.option.clear_rotating_msgs"] = "Clear All Messages",
                ["chat.enter_rotating_message"] = "Type message to add to rotating board ('cancel' to abort):",
                ["chat.rotating_message_added"] = "Added to rotating board: \"{0}\"",
                ["chat.rotating_messages_cleared"] = "All rotating messages cleared for '{0}'.",

                // Interactive Trigger
                ["menu.option.interact_menu"] = "Interactive Trigger (+USE/Shoot)",
                ["menu.title.interact"] = "Interactive Trigger: {0}",
                ["menu.option.interact_toggle"] = "Interaction Active",
                ["menu.option.interact_trigger_type"] = "Trigger: {0}",
                ["trigger.type.both"] = "E Key (+use) & Shoot",
                ["trigger.type.use"] = "E Key Only (+use)",
                ["trigger.type.shoot"] = "Shoot Only",
                ["menu.option.set_interact_msg"] = "Set Player Message (via chat)",
                ["menu.option.set_interact_cmd"] = "Set Command to Run (via chat)",
                ["menu.option.interact_dist"] = "Max Distance: {0} units",
                ["menu.option.interact_cooldown"] = "Cooldown: {0}s",
                ["chat.enter_interact_msg"] = "Type message to show in player chat when clicked ('cancel' to abort):",
                ["chat.enter_interact_cmd"] = "Type command to execute when clicked (e.g. '!vip', 'say !vip' or 'server: sv_cheats 1', 'cancel' to abort):",
                ["chat.interact_msg_set"] = "Trigger message updated to: \"{0}\"",
                ["chat.interact_cmd_set"] = "Trigger command updated to: \"{0}\"",

                // Menus
                ["menu.title.main"] = "World Text Management",
                ["menu.option.create"] = "Create New (aim & shoot)",
                ["menu.option.manage"] = "Manage Saved World Texts",
                ["menu.title.list"] = "Saved World Texts",
                ["menu.option.no_items"] = "No world texts saved.",
                ["menu.title.detail"] = "World Text: {0}",
                ["menu.option.enabled"] = "Enabled",
                ["menu.option.teleport"] = "Teleport to Text",
                ["menu.option.reaim"] = "Reposition (Aim & Shoot)",
                ["menu.option.duplicate"] = "Duplicate / Clone",
                ["menu.option.rename"] = "Rename Id (type in chat)",
                ["menu.option.change_text"] = "Change Text (type in chat)",
                ["menu.option.color_menu"] = "Color ({0})",
                ["menu.option.font"] = "Font: {0}",
                ["menu.option.face_player"] = "Face Player (Billboard)",
                ["menu.option.fullbright"] = "Fullbright",
                ["menu.option.justify"] = "Horizontal Align: {0}",
                ["menu.option.justify_left"] = "Left",
                ["menu.option.justify_center"] = "Center",
                ["menu.option.justify_right"] = "Right",
                ["menu.option.justify_vert"] = "Vertical Align: {0}",
                ["menu.option.justify_bottom"] = "Bottom",
                ["menu.option.justify_top"] = "Top",
                ["menu.option.fine_tune"] = "Fine-Tune Position & Angles",
                ["menu.option.position_x"] = "Move X",
                ["menu.option.position_y"] = "Move Y",
                ["menu.option.position_z"] = "Move Z (up/down)",
                ["menu.option.rotation_pitch"] = "Rotate Pitch",
                ["menu.option.rotation_yaw"] = "Rotate Yaw",
                ["menu.option.rotation_roll"] = "Rotate Roll",
                ["menu.option.font_size"] = "Font Size",
                ["menu.option.scale"] = "Scale",
                ["menu.option.background"] = "Background Panel",
                ["menu.option.delete"] = "DELETE (cannot be undone)",
                ["menu.option.back"] = "Back",

                ["menu.title.color"] = "Select Color ({0})",
                ["menu.color.white"] = "White (#FFFFFF)",
                ["menu.color.red"] = "Red (#FF3333)",
                ["menu.color.green"] = "Green (#33FF33)",
                ["menu.color.blue"] = "Blue (#3388FF)",
                ["menu.color.yellow"] = "Yellow (#FFFF33)",
                ["menu.color.cyan"] = "Cyan (#00FFFF)",
                ["menu.color.orange"] = "Orange (#FFA500)",
                ["menu.color.magenta"] = "Purple/Magenta (#DA70D6)",
                ["menu.color.gold"] = "Gold (#FFD700)",
                ["menu.color.pink"] = "Pink (#FF69B4)",
                ["menu.color.lime"] = "Lime (#32CD32)",
                ["menu.color.grey"] = "Grey (#888888)",
                ["menu.color.custom"] = "Custom HEX (type in chat)",

                ["menu.title.fine_tune"] = "Fine-Tune: {0}",
                ["menu.option.nudge_wall_right"] = "Slide Right along Wall (+{0})",
                ["menu.option.nudge_wall_left"] = "Slide Left along Wall (-{0})",
                ["menu.option.nudge_wall_out"] = "Pull Out from Wall (+{0})",
                ["menu.option.nudge_wall_in"] = "Push In towards Wall (-{0})",
                ["menu.option.nudge_x_pos"] = "Move +X (+{0})",
                ["menu.option.nudge_x_neg"] = "Move -X (-{0})",
                ["menu.option.nudge_y_pos"] = "Move +Y (+{0})",
                ["menu.option.nudge_y_neg"] = "Move -Y (-{0})",
                ["menu.option.nudge_z_up"] = "Move Up +Z (+{0})",
                ["menu.option.nudge_z_down"] = "Move Down -Z (-{0})",
                ["menu.option.nudge_yaw_left"] = "Rotate Left (Yaw -{0} deg)",
                ["menu.option.nudge_yaw_right"] = "Rotate Right (Yaw +{0} deg)",
                ["menu.option.nudge_pitch_up"] = "Pitch Up (+{0} deg)",
                ["menu.option.nudge_pitch_down"] = "Pitch Down (-{0} deg)",
                ["menu.option.nudge_roll_pos"] = "Roll Right (+{0} deg)",
                ["menu.option.nudge_roll_neg"] = "Roll Left (-{0} deg)",
                ["menu.option.straighten"] = "Straighten (Snap to Wall / Floor)",
                ["menu.option.step_size"] = "Step Size: {0} units",
            },
            ["tr"] = new()
            {
                ["chat.prefix"] = "[lime]WallText[default]",
                ["chat.no_permission"] = "Bu komutu kullanmak icin yetkiniz yok.",
                ["chat.usage_general"] = "Kullanim: !text [add|edit|delete|list|tp|reload|help] <isim>",
                ["chat.usage_add"] = "Kullanim: !text add <isim>",
                ["chat.usage_edit_cmd"] = "Kullanim: !text edit <isim>",
                ["chat.usage_delete"] = "Kullanim: !text delete <isim>",
                ["chat.usage_tp"] = "Kullanim: !text tp <isim>",
                ["chat.already_exists"] = "'{0}' isminde bir world text zaten var.",
                ["chat.not_found"] = "'{0}' bulunamadi.",
                ["chat.capturing_point"] = "'{0}' icin yazinin olacagi duvara bak ve ates et (bicak da olur).",
                ["chat.enter_text"] = "Simdi gorunmesini istediginiz metni sohbete yazin (bosluk kullanabilirsiniz, '\\n' yeni satir yapar, iptal icin 'cancel' yazin).",
                ["chat.created"] = "'{0}' olusturuldu, metin: \"{1}\".",
                ["chat.text_changed"] = "Metin guncellendi: \"{0}\".",
                ["chat.enter_new_name"] = "Simdi sohbete yeni ismi yazin (iptal etmek icin 'cancel' yazin).",
                ["chat.renamed"] = "'{0}' olarak yeniden adlandirildi.",
                ["chat.rename_cancelled"] = "Iptal edildi.",
                ["chat.deleted"] = "'{0}' silindi.",
                ["chat.list_header"] = "{0} haritasindaki world text'ler:",
                ["chat.list_empty"] = "Bu haritada henuz kayitli world text yok.",
                ["chat.console_only"] = "Bu komut sadece oyun icinden kullanilabilir.",
                ["chat.already_capturing"] = "Zaten devam eden bir nokta secimi var.",
                ["chat.reaim_prompt"] = "'{0}' icin yeni konuma bakip ates edin (veya bicak vurun).",
                ["chat.reaim_success"] = "'{0}' basariyla dik olarak yeni duvara hizalandi.",
                ["chat.teleported"] = "'{0}' konumuna isinlandiniz.",
                ["chat.duplicated"] = "'{0}', '{1}' metninden kopyalandi. Konumlandirmak icin duvara bakip ates edin.",
                ["chat.enter_color_hex"] = "Sohbete HEX renk kodunu yazin (ornek: #FF5500 veya FF5500, iptal icin 'cancel').",
                ["chat.color_changed"] = "Renk {0} olarak ayarlandi.",
                ["chat.invalid_color_hex"] = "Gecersiz HEX formati. Ornek: #FF0000 veya #FFFFFF.",
                ["chat.reloaded"] = "Yapilandirma ve '{0}' haritasi metinleri yenilendi ({1} adet yuklendi).",
                ["chat.help_header"] = "--- [lime]WallText Komutlari[default] ---",
                ["chat.help_add"] = "!text add <isim> - Yeni metin olustur (bak ve ates et)",
                ["chat.help_edit"] = "!text edit <isim> - Metin ismini degistir",
                ["chat.help_delete"] = "!text delete <isim> - Metni sil",
                ["chat.help_list"] = "!text list - Mevcut haritadaki tum metinleri listele",
                ["chat.help_tp"] = "!text tp <isim> - Metnin konumuna isinlan",
                ["chat.help_reload"] = "!text reload - Eklenti ayarlarini ve metinleri yenile",
                ["chat.help_menu"] = "!text - Yonetim menusunu ac",

                // Orientation & alignment
                ["chat.moved_to_crosshair"] = "'{0}' baktiginiz duvara dik olarak tasindi.",
                ["menu.option.orientation_menu"] = "Yön & Hızlı Açı Araçları",
                ["menu.title.orientation"] = "Yön & Hizalama: {0}",
                ["menu.option.pitch_up"] = "Yukarı Kaldır / Dik Yap (Pitch +90 der)",
                ["menu.option.pitch_down"] = "Aşağı Yatır (Pitch -90 der)",
                ["menu.option.roll_rotate"] = "Yan Yatır (Roll +90 der)",
                ["menu.option.preset_vertical"] = "Ayaklandır: Dik (Pitch 90 der)",
                ["menu.option.preset_vertical_neg"] = "Ayaklandır: Dik (Pitch -90 der)",
                ["menu.option.preset_wall_roll90"] = "Duvara Dik: Standart (Roll 90 der)",
                ["menu.option.preset_flat"] = "Zemine Yatır: Düz (Pitch 0, Roll 0)",
                ["menu.option.snap_upright"] = "Duvara Dik Yap (Pitch/Roll = 0)",
                ["menu.option.rotate_90"] = "90 der Sağa Çevir (+90 der)",
                ["menu.option.flip_180"] = "180 der Tersine Çevir",
                ["menu.option.face_admin"] = "Yüzünü Bana Çevir (Yatay)",
                ["menu.option.face_admin_3d"] = "Bakış Açıma Tam Hizala (3D)",
                ["menu.option.move_to_crosshair"] = "Baktığım Duvara Taşı (Anında)",

                // Rainbow
                ["menu.option.rainbow"] = "Rainbow (RGB): {0}",
                ["menu.option.rainbow_speed"] = "Rainbow Hızı: {0}x",

                // Rotating announcer
                ["menu.option.rotating_menu"] = "Dönen Duyuru Panosu ({0} Mesaj)",
                ["menu.title.rotating"] = "Duyuru Panosu: {0}",
                ["menu.option.rotating_toggle"] = "Dönüşümlü Duyuru Aktif",
                ["menu.option.rotating_interval"] = "Süre: {0} saniye",
                ["menu.option.add_rotating_msg"] = "Yeni Duyuru Ekle (Sohbetten)",
                ["menu.option.clear_rotating_msgs"] = "Tüm Duyuruları Temizle",
                ["chat.enter_rotating_message"] = "Dönen panoya eklenecek mesajı sohbete yazın ('cancel' ile iptal):",
                ["chat.rotating_message_added"] = "Dönen duyuru panosuna eklendi: \"{0}\"",
                ["chat.rotating_messages_cleared"] = "'{0}' için tüm dönüşümlü mesajlar temizlendi.",

                // Interactive Trigger
                ["menu.option.interact_menu"] = "Etkileşimli Buton (+USE/Ateş)",
                ["menu.title.interact"] = "Etkileşim: {0}",
                ["menu.option.interact_toggle"] = "Etkileşim Aktif",
                ["menu.option.interact_trigger_type"] = "Tetikleyici: {0}",
                ["trigger.type.both"] = "E Tuşu & Ateş Etme",
                ["trigger.type.use"] = "Sadece E Tuşu (+use)",
                ["trigger.type.shoot"] = "Sadece Ateş Etme",
                ["menu.option.set_interact_msg"] = "Oyuncuya Mesaj Ayarla (Sohbetten)",
                ["menu.option.set_interact_cmd"] = "Çalışacak Komutu Ayarla (Sohbetten)",
                ["menu.option.interact_dist"] = "Maks Mesafe: {0} birim",
                ["menu.option.interact_cooldown"] = "Bekleme Süresi: {0} sn",
                ["chat.enter_interact_msg"] = "Yazıya basıldığında oyuncunun sohbetine gidecek mesajı yazın ('cancel' ile iptal):",
                ["chat.enter_interact_cmd"] = "Yazıya basıldığında çalışacak komutu yazın (Örn: '!vip', 'say !vip' veya 'server: give weapon_ak47', iptal için 'cancel'):",
                ["chat.interact_msg_set"] = "Tetikleme mesajı güncellendi: \"{0}\"",
                ["chat.interact_cmd_set"] = "Tetikleme komutu güncellendi: \"{0}\"",

                // Menus
                ["menu.title.main"] = "World Text Yonetimi",
                ["menu.option.create"] = "Yeni Olustur (bak ve ates et)",
                ["menu.option.manage"] = "Kayitli World Text'leri Yonet",
                ["menu.title.list"] = "Kayitli World Text'ler",
                ["menu.option.no_items"] = "Kayitli world text yok.",
                ["menu.title.detail"] = "World Text: {0}",
                ["menu.option.enabled"] = "Aktif",
                ["menu.option.teleport"] = "Yaziya Isinlan",
                ["menu.option.reaim"] = "Yeniden Konumlandir (Bak & Ates Et)",
                ["menu.option.duplicate"] = "Kopyala / Cogalt",
                ["menu.option.rename"] = "Id'yi Yeniden Adlandir (sohbete yaz)",
                ["menu.option.change_text"] = "Metni Degistir (sohbete yaz)",
                ["menu.option.color_menu"] = "Renk ({0})",
                ["menu.option.font"] = "Yazı Tipi: {0}",
                ["menu.option.face_player"] = "Oyuncuya Don (Billboard)",
                ["menu.option.fullbright"] = "Fullbright (Parlaklik)",
                ["menu.option.justify"] = "Yatay Hizalama: {0}",
                ["menu.option.justify_left"] = "Sol",
                ["menu.option.justify_center"] = "Orta",
                ["menu.option.justify_right"] = "Sag",
                ["menu.option.justify_vert"] = "Dikey Hizalama: {0}",
                ["menu.option.justify_bottom"] = "Alt",
                ["menu.option.justify_top"] = "Üst",
                ["menu.option.fine_tune"] = "Hassas Konum & Aci Ayari",
                ["menu.option.position_x"] = "X Ekseninde Kaydir",
                ["menu.option.position_y"] = "Y Ekseninde Kaydir",
                ["menu.option.position_z"] = "Yukari/Asagi Kaydir",
                ["menu.option.rotation_pitch"] = "Egim (Pitch) Dondur",
                ["menu.option.rotation_yaw"] = "Yatay (Yaw) Dondur",
                ["menu.option.rotation_roll"] = "Yan (Roll) Dondur",
                ["menu.option.font_size"] = "Yazi Boyutu",
                ["menu.option.scale"] = "Olcek",
                ["menu.option.background"] = "Arka Plan Paneli",
                ["menu.option.delete"] = "SIL (geri alinamaz)",
                ["menu.option.back"] = "Geri",

                ["menu.title.color"] = "Renk Secimi ({0})",
                ["menu.color.white"] = "Beyaz (#FFFFFF)",
                ["menu.color.red"] = "Kirmizi (#FF3333)",
                ["menu.color.green"] = "Yesil (#33FF33)",
                ["menu.color.blue"] = "Mavi (#3388FF)",
                ["menu.color.yellow"] = "Sari (#FFFF33)",
                ["menu.color.cyan"] = "Camgobegi (#00FFFF)",
                ["menu.color.orange"] = "Turuncu (#FFA500)",
                ["menu.color.magenta"] = "Mor/Magenta (#DA70D6)",
                ["menu.color.gold"] = "Altin Sarisi (#FFD700)",
                ["menu.color.pink"] = "Pembe (#FF69B4)",
                ["menu.color.lime"] = "Acik Yesil (#32CD32)",
                ["menu.color.grey"] = "Gri (#888888)",
                ["menu.color.custom"] = "Ozel HEX Gir (Sohbetten)",

                ["menu.title.fine_tune"] = "Hassas Ayar: {0}",
                ["menu.option.nudge_wall_right"] = "Duvara Göre Sağa Kaydır (+{0})",
                ["menu.option.nudge_wall_left"] = "Duvara Göre Sola Kaydır (-{0})",
                ["menu.option.nudge_wall_out"] = "Duvardan Dışarı Çek (+{0})",
                ["menu.option.nudge_wall_in"] = "Duvara Doğru İt (-{0})",
                ["menu.option.nudge_x_pos"] = "+X Kaydir (+{0})",
                ["menu.option.nudge_x_neg"] = "-X Kaydir (-{0})",
                ["menu.option.nudge_y_pos"] = "+Y Kaydir (+{0})",
                ["menu.option.nudge_y_neg"] = "-Y Kaydir (-{0})",
                ["menu.option.nudge_z_up"] = "Yukari +Z (+{0})",
                ["menu.option.nudge_z_down"] = "Asagi -Z (-{0})",
                ["menu.option.nudge_yaw_left"] = "Sola Dondur (Yaw -{0} der)",
                ["menu.option.nudge_yaw_right"] = "Saga Dondur (Yaw +{0} der)",
                ["menu.option.nudge_pitch_up"] = "Yukarı Kaldır (+{0} der Pitch)",
                ["menu.option.nudge_pitch_down"] = "Aşağı Yatır (-{0} der Pitch)",
                ["menu.option.nudge_roll_pos"] = "Sağa Yatır (+{0} der Roll)",
                ["menu.option.nudge_roll_neg"] = "Sola Yatır (-{0} der Roll)",
                ["menu.option.straighten"] = "Duzlestir (Duvara / Zemine Hizala)",
                ["menu.option.step_size"] = "Ayar Adimi: {0} birim",
            },
        };
    }
}
