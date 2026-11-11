using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

namespace CS2WallText;

public partial class WallTextModule
{
    private void ToggleMainMenu(IPlayer player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient)
            return;

        long now = Environment.TickCount64;
        if (_lastToggleTick.TryGetValue(player.Slot, out long prev) && now - prev < 150)
            return;
        _lastToggleTick[player.Slot] = now;

        if (_core.Menu.GetSession(player) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
        {
            CloseMenu(player);
            return;
        }

        OpenMainMenu(player);
    }

    private void OpenMainMenu(IPlayer player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient)
            return;

        CloseMenu(player);
        var main = BuildMainMenu(player.Slot);
        main.Open(player);
    }

    private void OpenListMenu(IPlayer player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient)
            return;

        CloseMenu(player);
        var main = BuildMainMenu(player.Slot);
        var list = BuildListMenu(player.Slot, main);
        list.Parent = main;
        list.Open(player);
    }

    private void OpenDetailMenu(IPlayer player, string name)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        CloseMenu(player);
        var main = BuildMainMenu(player.Slot);
        var list = BuildListMenu(player.Slot, main);
        list.Parent = main;
        var detail = BuildDetailMenu(player.Slot, data, list);
        detail.Parent = list;
        detail.Open(player);
    }

    private void OpenOrientationMenu(IPlayer player, string name)
    {
        OpenChildForText(player, name, (slot, data, detail) =>
        {
            var sub = BuildOrientationMenu(slot, data);
            sub.Parent = detail;
            return sub;
        });
    }

    private void OpenColorMenu(IPlayer player, string name)
    {
        OpenChildForText(player, name, (slot, data, detail) =>
        {
            var sub = BuildColorMenu(slot, data);
            sub.Parent = detail;
            return sub;
        });
    }

    private void OpenRotatingMenu(IPlayer player, string name)
    {
        OpenChildForText(player, name, (slot, data, detail) =>
        {
            var sub = BuildRotatingMenu(slot, data);
            sub.Parent = detail;
            return sub;
        });
    }

    private void OpenInteractMenu(IPlayer player, string name)
    {
        OpenChildForText(player, name, (slot, data, detail) =>
        {
            var sub = BuildInteractMenu(slot, data);
            sub.Parent = detail;
            return sub;
        });
    }

    private void OpenChildForText(IPlayer player, string name, Func<int, WallTextData, IMenu, IMenu> childFactory)
    {
        var key = FindKey(name);
        if (key == null || !_store.TryGetValue(key, out var data))
        {
            OpenListMenu(player);
            return;
        }

        CloseMenu(player);
        var main = BuildMainMenu(player.Slot);
        var list = BuildListMenu(player.Slot, main);
        list.Parent = main;
        var detail = BuildDetailMenu(player.Slot, data, list);
        detail.Parent = list;
        var child = childFactory(player.Slot, data, detail);
        child.Open(player);
    }

    private void CloseMenu(IPlayer player)
    {
        if (player != null && player.IsValid)
        {
            if (_core.Menu.GetSession(player) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
            {
                _core.Menu.CloseFor(player);
            }
            DisposeSlotMenus(player.Slot);
        }
    }

    private void CloseAllMenus()
    {
        try
        {
            foreach (var player in _core.PlayerManager.GetAllPlayers())
            {
                if (player != null && player.IsValid && !player.IsFakeClient)
                {
                    if (_core.Menu.GetSession(player) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
                    {
                        _core.Menu.CloseFor(player);
                    }
                }
            }
        }
        catch
        {
            // Ignore during shutdown
        }

        foreach (int slot in new List<int>(_ownedMenus.Keys))
        {
            DisposeSlotMenus(slot);
        }
        _ownedMenus.Clear();
    }

    private IMenu Track(int slot, IMenu menu)
    {
        menu.Tag = MenuTag;
        if (!_ownedMenus.TryGetValue(slot, out var list))
        {
            list = new List<IMenu>();
            _ownedMenus[slot] = list;
        }
        list.Add(menu);
        return menu;
    }

    private void DisposeSlotMenus(int slot)
    {
        if (!_ownedMenus.Remove(slot, out var list))
            return;

        foreach (var menu in list)
        {
            try { menu.Dispose(); } catch { }
        }
        list.Clear();
    }

    private string GenerateUniqueDefaultName()
    {
        int idx = _store.Count + 1;
        string candidate = $"Text_{idx}";
        while (FindKey(candidate) != null)
        {
            idx++;
            candidate = $"Text_{idx}";
        }
        return candidate;
    }

    private float GetStepSize(int slot)
    {
        if (!_playerStepSizes.TryGetValue(slot, out var step))
        {
            step = 5f;
            _playerStepSizes[slot] = step;
        }
        return step;
    }

    private static string SafePreview(string? text, int maxLen = 28)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = HtmlTagRegex.Replace(text, " ");
        clean = clean.Replace("<", "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (clean.Contains("  ", StringComparison.Ordinal))
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);

        if (clean.Length == 0) return "(boş)";
        return clean.Length > maxLen ? clean[..maxLen] + "..." : clean;
    }

    private static void CloseTree(IMenuSession session)
    {
        var menu = session.Menu;
        var parent = menu.Parent;
        menu.Parent = null;
        session.Close();
        menu.Parent = parent;
    }

    private static void ReplaceRows(IMenu menu, PageState page, IReadOnlyList<IMenuComponent> rows)
    {
        foreach (var row in page.Rows)
            menu.Remove(row);
        page.Rows.Clear();

        for (int i = 0; i < rows.Count; i++)
        {
            menu.Insert(MenuRegion.Body, page.RowsAt + i, rows[i]);
            page.Rows.Add(rows[i]);
        }
    }

    private static void RestoreFocus(IMenuSession session, PageState page)
    {
        if (page.FocusKey is { } key && FocusWhere(session, c => c is MenuComponentBase { Tag: { } tag } && tag.Equals(key)))
            return;

        if (page.Focus is { } focus)
        {
            for (int i = focus; i > 0; i--)
            {
                if (session.FocusedIndex == i || session.SetFocus(i))
                    return;
            }
        }
    }

    private static bool FocusWhere(IMenuSession session, Func<IMenuComponent, bool> match)
    {
        int index = 0;
        foreach (var component in session.Menu.GetComponents(MenuRegion.Body))
        {
            if (!component.IsFocusable || !component.IsVisible(session))
                continue;

            if (match(component))
            {
                if (session.FocusedIndex != index)
                    session.SetFocus(index);
                return true;
            }

            index++;
        }

        return false;
    }

    // ============================== 1. ANA MENÜ ==============================

    private IMenu BuildMainMenu(int ownerSlot)
    {
        IMenu? mainMenu = null;
        bool syncingCreate = false;
        InputComponent? customCreateInput = null;

        customCreateInput = new InputComponent(
            "İsimle Oluştur (Nişangah)",
            defaultValue: "",
            validator: raw =>
            {
                var trimmed = raw.Trim();
                return !string.IsNullOrWhiteSpace(trimmed) && FindKey(trimmed) == null;
            },
            onChanged: change =>
            {
                if (syncingCreate) return;
                var newName = change.NewValue.Trim();
                syncingCreate = true;
                customCreateInput?.SetValue(change.Session, "");
                syncingCreate = false;

                var p = change.Player;
                CloseTree(change.Session);
                _core.Scheduler.NextTick(() => BeginCreate(p, newName));
            },
            id: "create_named")
        {
            MaxLength = 48,
            EmptyText = "ID Yaz",
            InvalidText = "Bu isim geçersiz veya zaten var",
            HintMessage = "[gold]Oluşturulacak duvar yazısının ID adını yazın[default] (sonra duvara ateş edin)"
        };

        mainMenu = Track(ownerSlot, _core.Menu.CreateMenu("cs2walltext.main")
            .WithTitle(SafePreview(T("menu.title.main"), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Harita: {_mapContext.CurrentMap} • Kayıtlı: {_store.Count}"
            })
            .Add(new ButtonComponent(T("menu.option.create"), ctx =>
            {
                var p = ctx.Player;
                CloseTree(ctx.Session);
                _core.Scheduler.NextTick(() => BeginCreate(p, GenerateUniqueDefaultName()));
                return ValueTask.CompletedTask;
            }))
            .Add(customCreateInput)
            .Add(new SubmenuComponent("Hazır Şablonlar\t5 Şablon", ctx =>
            {
                var sub = BuildTemplatesMenu(ownerSlot, mainMenu);
                sub.Parent = ctx.Menu;
                return sub;
            }))
            .Add(new SubmenuComponent(T("menu.option.manage"), ctx =>
            {
                var sub = BuildListMenu(ownerSlot, mainMenu);
                sub.Parent = ctx.Menu;
                return sub;
            })
            {
                TextProvider = _ => $"{T("menu.option.manage")}\t{_store.Count} kayıt"
            })
            .Add(new ButtonComponent("Harita Yazılarını Yeniden Yükle", ctx =>
            {
                LoadConfig();
                ReloadForCurrentMap();
                SendColoredChat(ctx.Player, $"{Prefix()} {T("chat.reloaded", _mapContext.CurrentMap, _store.Count)}");
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build());

        return mainMenu;
    }

    // ============================== 2. HAZIR ŞABLONLAR ==============================

    private IMenu BuildTemplatesMenu(int ownerSlot, IMenu? mainMenu)
    {
        var templates = new (string Label, string Text, string Color, bool Rainbow, float FontSize)[]
        {
            ("Discord Topluluk Panosu", "✦ DISCORD TOPLULUĞUMUZ ✦\ndiscord.gg/sunucu", "#38BDF8", false, 36f),
            ("Hoş Geldiniz & Sunucu Panosu", "★ {SERVER_NAME} ★\nHarita: {MAP} | Oyuncu: {PLAYERS}/{MAXPLAYERS}", "#FFD700", true, 38f),
            ("Sunucu Kuralları Panosu", "⚖ SUNUCU KURALLARI ⚖\n1. Hile ve 3. Parti Yazılım Yasaktır\n2. Küfür ve Hakaret Yasaktır\n3. İyi Oyunlar!", "#FF3333", false, 30f),
            ("Canlı Saat & Tarih Panosu", "🕒 SAAT: {TIME}\n📅 TARİH: {DATE}", "#33FF33", false, 34f),
            ("Etkileşimli VIP Bilgi Butonu (+USE)", "★ VIP BİLGİ ★\n[E] Tuşuna Basarak İnceleyin!", "#DA70D6", true, 34f)
        };

        var builder = _core.Menu.CreateMenu("cs2walltext.templates")
            .WithTitle("HAZIR DUVAR YAZISI ŞABLONLARI")
            .AddHeader(new TextComponent("Seçtikten sonra duvara ateş ederek yerleştirin"));

        for (int i = 0; i < templates.Length; i++)
        {
            var tpl = templates[i];
            int idx = i;
            builder.Add(new ButtonComponent(tpl.Label, ctx =>
            {
                var p = ctx.Player;
                string name = GenerateUniqueDefaultName();
                CloseTree(ctx.Session);

                _core.Scheduler.NextTick(() =>
                {
                    if (!p.IsValid) return;
                    SendColoredChat(p, $"{Prefix()} {T("chat.capturing_point", name)}");
                    BeginPointCapture(p, (adm, point, normal) =>
                    {
                        var placedPos = point + normal * WallOffset;
                        var angle = CalculateWallAngles(normal, adm.PlayerPawn?.EyeAngles ?? default);
                        var data = new WallTextData
                        {
                            Name = name,
                            Text = tpl.Text,
                            Position = [placedPos.X, placedPos.Y, placedPos.Z],
                            Angles = [angle.Pitch, angle.Yaw, angle.Roll],
                            Enabled = true,
                            ColorHex = tpl.Color,
                            Rainbow = tpl.Rainbow,
                            FontSize = tpl.FontSize,
                            Scale = 0.25f,
                            Fullbright = true,
                            IsInteractable = idx == 4,
                            TriggerType = "use",
                            MessageToPlayer = idx == 4 ? "[gold]★ VIP Ayrıcalıkları için Discord sunucumuza katılın![default]" : ""
                        };
                        _store[name] = data;
                        Save();
                        SpawnEntity(data);
                        SendColoredChat(adm, $"{Prefix()} {T("chat.created", name, data.Text.Replace("\n", " "))}");
                        _core.Scheduler.NextTick(() => OpenDetailMenu(adm, name));
                    });
                });

                return ValueTask.CompletedTask;
            }));
        }

        return Track(ownerSlot, builder.Build());
    }

    // ============================== 3. KAYITLI YAZILAR ==============================

    private IMenu BuildListMenu(int ownerSlot, IMenu? mainMenu)
    {
        var page = new PageState { RowsAt = 0 };
        IMenu? listMenu = null;

        var builder = _core.Menu.CreateMenu("cs2walltext.list")
            .WithTitle(SafePreview(T("menu.title.list"), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Toplam {_store.Count} duvar yazısı • Harita: {_mapContext.CurrentMap}"
            });

        listMenu = Track(ownerSlot, builder.Build());

        void Populate()
        {
            var rows = new List<IMenuComponent>();
            foreach (var name in new List<string>(_store.Keys))
            {
                if (!_store.TryGetValue(name, out var data))
                    continue;

                var capturedData = data;
                rows.Add(new SubmenuComponent("", ctx =>
                {
                    var detail = BuildDetailMenu(ownerSlot, capturedData, listMenu);
                    detail.Parent = ctx.Menu;
                    return detail;
                })
                {
                    Tag = capturedData.Name,
                    TextProvider = _ =>
                    {
                        string status = capturedData.Enabled ? "ON" : "OFF";
                        string preview = SafePreview(capturedData.Text, 18);
                        return $"{SafePreview(capturedData.Name, 18)}\t\"{preview}\" [{status}]";
                    }
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new TextComponent(T("menu.option.no_items")));
            }

            ReplaceRows(listMenu, page, rows);
        }

        listMenu.Opened += session =>
        {
            Populate();
            RestoreFocus(session, page);
        };

        listMenu.FocusChanged += session =>
        {
            page.Focus = session.FocusedIndex;
            page.FocusKey = (session.FocusedComponent as MenuComponentBase)?.Tag;
        };

        return listMenu;
    }

    // ============================== 4. YAZI DETAY MENÜSÜ ==============================

    private IMenu BuildDetailMenu(int ownerSlot, WallTextData data, IMenu? listMenu)
    {
        IMenu? detailMenu = null;
        bool syncingText = false;
        bool syncingRename = false;
        InputComponent? textInput = null;
        InputComponent? renameInput = null;

        textInput = new InputComponent(
            "Metni Değiştir",
            defaultValue: SafePreview(data.Text.Replace("\n", "\\n"), 22),
            validator: v => !string.IsNullOrWhiteSpace(v),
            onChanged: change =>
            {
                if (syncingText) return;
                syncingText = true;
                data.Text = change.NewValue.Trim().Replace("\\n", "\n");
                Save();
                UpdateLiveEntity(data);
                textInput?.SetValue(change.Session, SafePreview(data.Text.Replace("\n", "\\n"), 22));
                syncingText = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.text_changed", data.Text)}");
            })
        {
            MaxLength = 256,
            EmptyText = "Metin",
            InvalidText = "Metin boş olamaz",
            DefaultValueProvider = _ => SafePreview(data.Text.Replace("\n", "\\n"), 22),
            HintMessage = "[gold]Yeni duvar yazısını yazın[default] (alt satır için [lime]\\n[default] kullanın)"
        };

        renameInput = new InputComponent(
            "ID Yeniden Adlandır",
            defaultValue: SafePreview(data.Name, 22),
            validator: raw =>
            {
                var candidate = raw.Trim();
                if (string.IsNullOrWhiteSpace(candidate)) return false;
                var existing = FindKey(candidate);
                return existing == null || existing.Equals(data.Name, StringComparison.OrdinalIgnoreCase);
            },
            onChanged: change =>
            {
                if (syncingRename) return;
                string newName = change.NewValue.Trim();
                string oldName = data.Name;

                if (!newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                {
                    var oldKey = FindKey(oldName);
                    if (oldKey != null)
                        _store.Remove(oldKey);

                    data.Name = newName;
                    _store[newName] = data;
                    Save();

                    if (oldKey != null)
                    {
                        if (_liveEntities.Remove(oldKey, out var entity))
                            _liveEntities[newName] = entity;
                        if (_dynamicTextRenderCache.Remove(oldKey, out var cached))
                            _dynamicTextRenderCache[newName] = cached;
                        if (_rotatingState.Remove(oldKey, out var rot))
                            _rotatingState[newName] = rot;
                    }
                }

                syncingRename = true;
                renameInput?.SetValue(change.Session, SafePreview(data.Name, 22));
                syncingRename = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.renamed", data.Name)}");
            })
        {
            MaxLength = 48,
            EmptyText = "ID",
            InvalidText = "Bu ID geçersiz veya başka bir yazıda kullanılıyor",
            DefaultValueProvider = _ => SafePreview(data.Name, 22),
            HintMessage = "[gold]Yeni ID adını yazın[default]"
        };

        int fontIdx = Math.Max(0, Array.IndexOf(AvailableFonts, data.FontName ?? ""));
        int scaleIdx = Array.FindIndex(AvailableScales, s => MathF.Abs(s - data.Scale) < 0.025f);
        if (scaleIdx < 0) scaleIdx = 4;

        detailMenu = Track(ownerSlot, _core.Menu.CreateMenu("cs2walltext.detail")
            .WithTitle(SafePreview(T("menu.title.detail", data.Name), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"{SafePreview(data.Name, 20)} • [P:{data.Angles[0]:0}° Y:{data.Angles[1]:0}° R:{data.Angles[2]:0}°] • {(data.Rainbow ? "RGB" : data.ColorHex)}"
            })
            .Add(new ToggleComponent(T("menu.option.enabled"), data.Enabled, change =>
            {
                data.Enabled = change.NewValue;
                Save();
                if (data.Enabled) SpawnEntity(data);
                else DespawnEntity(data.Name);
                change.Session.Invalidate();
            })
            {
                DefaultValueProvider = _ => data.Enabled
            })
            .Add(new ButtonComponent(T("menu.option.move_to_crosshair"), ctx =>
            {
                if (MoveToCrosshair(ctx.Player, data.Name))
                    ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new SubmenuComponent(T("menu.option.orientation_menu"), ctx =>
            {
                var sub = BuildOrientationMenu(ownerSlot, data);
                sub.Parent = ctx.Menu;
                return sub;
            }))
            .Add(new SubmenuComponent(T("menu.option.fine_tune"), ctx =>
            {
                var sub = BuildFineTuneMenu(ownerSlot, data);
                sub.Parent = ctx.Menu;
                return sub;
            }))
            .Add(textInput)
            .Add(new SubmenuComponent("", ctx =>
            {
                var sub = BuildColorMenu(ownerSlot, data);
                sub.Parent = ctx.Menu;
                return sub;
            })
            {
                TextProvider = _ => T("menu.option.color_menu", data.Rainbow ? "RGB" : data.ColorHex)
            })
            .Add(new ToggleComponent("Rainbow (RGB)", data.Rainbow, change =>
            {
                data.Rainbow = change.NewValue;
                Save();
                UpdateLiveEntity(data);
                change.Session.Invalidate();
            })
            {
                DefaultValueProvider = _ => data.Rainbow
            })
            .Add(new SliderComponent(
                "Rainbow Hızı",
                0.5f,
                3.0f,
                Math.Clamp(data.RainbowSpeed, 0.5f, 3.0f),
                0.5f,
                change =>
                {
                    data.RainbowSpeed = MathF.Round(change.NewValue * 10f) / 10f;
                    Save();
                    change.Session.Invalidate();
                })
            {
                TotalBars = 5,
                ValueFormat = "0.0x",
                VisibleWhen = _ => data.Rainbow,
                DefaultValueProvider = _ => Math.Clamp(data.RainbowSpeed, 0.5f, 3.0f)
            })
            .Add(new SubmenuComponent("", ctx =>
            {
                var sub = BuildRotatingMenu(ownerSlot, data);
                sub.Parent = ctx.Menu;
                return sub;
            })
            {
                TextProvider = _ => T("menu.option.rotating_menu", data.RotatingMessages.Count)
            })
            .Add(new SubmenuComponent(T("menu.option.interact_menu"), ctx =>
            {
                var sub = BuildInteractMenu(ownerSlot, data);
                sub.Parent = ctx.Menu;
                return sub;
            })
            {
                TextProvider = _ => $"{T("menu.option.interact_menu")}\t{(data.IsInteractable ? "AKTİF" : "KAPALI")}"
            })
            .Add(new SelectorComponent<string>(
                "Yazı Tipi",
                AvailableFonts,
                fontIdx,
                change =>
                {
                    data.FontName = change.NewValue;
                    Save();
                    UpdateLiveEntity(data);
                })
            {
                DefaultValueProvider = _ => Math.Max(0, Array.IndexOf(AvailableFonts, data.FontName ?? "")),
                Formatter = f => string.IsNullOrWhiteSpace(f) ? "Default" : f
            })
            .Add(new SliderComponent(
                T("menu.option.font_size"),
                8f,
                72f,
                Math.Clamp(data.FontSize, 8f, 72f),
                2f,
                change =>
                {
                    data.FontSize = MathF.Round(change.NewValue);
                    Save();
                    UpdateLiveEntity(data);
                })
            {
                TotalBars = 10,
                ValueFormat = "0",
                DefaultValueProvider = _ => Math.Clamp(data.FontSize, 8f, 72f)
            })
            .Add(new SelectorComponent<float>(
                T("menu.option.scale"),
                AvailableScales,
                scaleIdx,
                change =>
                {
                    data.Scale = change.NewValue;
                    Save();
                    UpdateLiveEntity(data);
                })
            {
                DefaultValueProvider = _ =>
                {
                    int idx = Array.FindIndex(AvailableScales, s => MathF.Abs(s - data.Scale) < 0.025f);
                    return idx >= 0 ? idx : 4;
                },
                Formatter = s => $"{s:0.00}"
            })
            .Add(new SelectorComponent<int>(
                "Yatay Hizalama",
                [0, 1, 2],
                Math.Clamp(data.JustifyHorizontal, 0, 2),
                change =>
                {
                    data.JustifyHorizontal = change.NewValue;
                    Save();
                    UpdateLiveEntity(data);
                })
            {
                DefaultValueProvider = _ => Math.Clamp(data.JustifyHorizontal, 0, 2),
                Formatter = v => v switch
                {
                    0 => T("menu.option.justify_left"),
                    2 => T("menu.option.justify_right"),
                    _ => T("menu.option.justify_center")
                }
            })
            .Add(new SelectorComponent<int>(
                "Dikey Hizalama",
                [0, 1, 2],
                Math.Clamp(data.JustifyVertical, 0, 2),
                change =>
                {
                    data.JustifyVertical = change.NewValue;
                    Save();
                    UpdateLiveEntity(data);
                })
            {
                DefaultValueProvider = _ => Math.Clamp(data.JustifyVertical, 0, 2),
                Formatter = v => v switch
                {
                    0 => T("menu.option.justify_bottom"),
                    2 => T("menu.option.justify_top"),
                    _ => T("menu.option.justify_center")
                }
            })
            .Add(new ToggleComponent(T("menu.option.face_player"), data.FacePlayer, change =>
            {
                data.FacePlayer = change.NewValue;
                Save();
                UpdateLiveEntity(data);
            })
            {
                DefaultValueProvider = _ => data.FacePlayer
            })
            .Add(new ToggleComponent(T("menu.option.fullbright"), data.Fullbright, change =>
            {
                data.Fullbright = change.NewValue;
                Save();
                UpdateLiveEntity(data);
            })
            {
                DefaultValueProvider = _ => data.Fullbright
            })
            .Add(new ToggleComponent(T("menu.option.background"), data.Background, change =>
            {
                data.Background = change.NewValue;
                Save();
                UpdateLiveEntity(data);
            })
            {
                DefaultValueProvider = _ => data.Background
            })
            .Add(new ButtonComponent(T("menu.option.teleport"), ctx =>
            {
                TeleportToText(ctx.Player, data.Name);
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.reaim"), ctx =>
            {
                var p = ctx.Player;
                string target = data.Name;
                CloseTree(ctx.Session);
                _core.Scheduler.NextTick(() => BeginReaim(p, target));
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.duplicate"), ctx =>
            {
                var p = ctx.Player;
                string source = data.Name;
                CloseTree(ctx.Session);
                _core.Scheduler.NextTick(() => BeginDuplicate(p, source));
                return ValueTask.CompletedTask;
            }))
            .Add(renameInput)
            .Add(new DangerSubmenuComponent(T("menu.option.delete"), ctx =>
            {
                var confirm = BuildConfirmDeleteMenu(ownerSlot, data, listMenu);
                confirm.Parent = ctx.Menu;
                return confirm;
            }))
            .Build());

        return detailMenu;
    }

    private IMenu BuildConfirmDeleteMenu(int ownerSlot, WallTextData data, IMenu? listMenu)
    {
        IMenu? confirmMenu = null;

        confirmMenu = Track(ownerSlot, _core.Menu.CreateMenu("cs2walltext.confirm_delete")
            .WithTitle(SafePreview($"SİL: {data.Name}", 30))
            .AddHeader(new TextComponent($"'{SafePreview(data.Name, 24)}' kalıcı olarak silinecek."))
            .Add(new DangerButtonComponent("Evet, Kalıcı Olarak Sil", ctx =>
            {
                DeleteWallText(ctx.Player, data.Name);
                if (confirmMenu != null && listMenu != null)
                {
                    confirmMenu.Parent = listMenu;
                }
                ctx.Session.Close();
            }))
            .Add(new ButtonComponent("İptal", ctx =>
            {
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .Build());

        return confirmMenu;
    }

    // ============================== 5. YÖN & HİZALAMA ==============================

    private IMenu BuildOrientationMenu(int ownerSlot, WallTextData data)
    {
        var menu = _core.Menu.CreateMenu("cs2walltext.orientation")
            .WithTitle(SafePreview(T("menu.title.orientation", data.Name), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Açı: [P:{data.Angles[0]:0}° Y:{data.Angles[1]:0}° R:{data.Angles[2]:0}°]"
            })
            .Add(new ButtonComponent(T("menu.option.move_to_crosshair"), ctx =>
            {
                if (MoveToCrosshair(ctx.Player, data.Name))
                    ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.face_admin"), ctx =>
            {
                if (ctx.Player.PlayerPawn != null && ctx.Player.PlayerPawn.AbsOrigin.HasValue)
                {
                    var pPos = ctx.Player.PlayerPawn.AbsOrigin.Value;
                    float dx = pPos.X - data.Position[0];
                    float dy = pPos.Y - data.Position[1];
                    var (fp, fy, fr) = AnglesFacingDirection(dx, dy, 0f, keepUpright: true);
                    data.Angles[0] = fp;
                    data.Angles[1] = fy;
                    data.Angles[2] = fr;
                    Save();
                    UpdateLiveEntity(data);
                    ctx.Session.Invalidate();
                }
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.face_admin_3d"), ctx =>
            {
                if (ctx.Player.PlayerPawn != null && TryGetEyePosition(ctx.Player.PlayerPawn, out var eyePos))
                {
                    float dx = eyePos.X - data.Position[0];
                    float dy = eyePos.Y - data.Position[1];
                    float dz = eyePos.Z - data.Position[2];
                    var (fp, fy, fr) = AnglesFacingDirection(dx, dy, dz, keepUpright: false);
                    data.Angles[0] = fp;
                    data.Angles[1] = fy;
                    data.Angles[2] = fr;
                    Save();
                    UpdateLiveEntity(data);
                    ctx.Session.Invalidate();
                }
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.flip_180"), ctx =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] + 180f);
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.rotate_90"), ctx =>
            {
                data.Angles[1] = NormalizeAngle(data.Angles[1] + 90f);
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.pitch_up"), ctx =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] + 90f);
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.pitch_down"), ctx =>
            {
                data.Angles[0] = NormalizeAngle(data.Angles[0] - 90f);
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.roll_rotate"), ctx =>
            {
                data.Angles[2] = NormalizeAngle(data.Angles[2] + 90f);
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.preset_vertical"), ctx =>
            {
                data.Angles[0] = 90f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.preset_vertical_neg"), ctx =>
            {
                data.Angles[0] = -90f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.preset_wall_roll90"), ctx =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 90f;
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent(T("menu.option.preset_flat"), ctx =>
            {
                data.Angles[0] = 0f;
                data.Angles[2] = 0f;
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, menu);
    }

    // ============================== 6. HASSAS AYAR ==============================

    private IMenu BuildFineTuneMenu(int ownerSlot, WallTextData data)
    {
        float initialStep = GetStepSize(ownerSlot);
        int initialStepIdx = Math.Max(0, Array.IndexOf(AvailableSteps, initialStep));

        ButtonComponent MakeNudgeButton(string translationKey, Action<float> apply)
        {
            return new ButtonComponent("", ctx =>
            {
                apply(GetStepSize(ctx.Player.Slot));
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            })
            {
                TextProvider = _ => T(translationKey, GetStepSize(ownerSlot))
            };
        }

        var menu = _core.Menu.CreateMenu("cs2walltext.finetune")
            .WithTitle(SafePreview(T("menu.title.fine_tune", data.Name), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Adım: {GetStepSize(ownerSlot):0} • Pos: ({data.Position[0]:0}, {data.Position[1]:0}, {data.Position[2]:0}) • Açı: [{data.Angles[0]:0}°/{data.Angles[1]:0}°/{data.Angles[2]:0}°]"
            })
            .Add(new SelectorComponent<float>(
                "Ayar Adımı",
                AvailableSteps,
                initialStepIdx,
                change =>
                {
                    _playerStepSizes[change.Player.Slot] = change.NewValue;
                    change.Session.Invalidate();
                })
            {
                DefaultValueProvider = _ => Math.Max(0, Array.IndexOf(AvailableSteps, GetStepSize(ownerSlot))),
                Formatter = s => $"{s:0} birim"
            })
            .Add(MakeNudgeButton("menu.option.nudge_wall_right", step =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] += -MathF.Sin(yawRad) * step;
                data.Position[1] += MathF.Cos(yawRad) * step;
            }))
            .Add(MakeNudgeButton("menu.option.nudge_wall_left", step =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] -= -MathF.Sin(yawRad) * step;
                data.Position[1] -= MathF.Cos(yawRad) * step;
            }))
            .Add(MakeNudgeButton("menu.option.nudge_z_up", step => data.Position[2] += step))
            .Add(MakeNudgeButton("menu.option.nudge_z_down", step => data.Position[2] -= step))
            .Add(MakeNudgeButton("menu.option.nudge_wall_out", step =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] += MathF.Cos(yawRad) * step;
                data.Position[1] += MathF.Sin(yawRad) * step;
            }))
            .Add(MakeNudgeButton("menu.option.nudge_wall_in", step =>
            {
                float yawRad = FaceYaw(data) * (MathF.PI / 180f);
                data.Position[0] -= MathF.Cos(yawRad) * step;
                data.Position[1] -= MathF.Sin(yawRad) * step;
            }))
            .Add(MakeNudgeButton("menu.option.nudge_x_pos", step => data.Position[0] += step))
            .Add(MakeNudgeButton("menu.option.nudge_x_neg", step => data.Position[0] -= step))
            .Add(MakeNudgeButton("menu.option.nudge_y_pos", step => data.Position[1] += step))
            .Add(MakeNudgeButton("menu.option.nudge_y_neg", step => data.Position[1] -= step))
            .Add(MakeNudgeButton("menu.option.nudge_pitch_up", step => data.Angles[0] = NormalizeAngle(data.Angles[0] + step)))
            .Add(MakeNudgeButton("menu.option.nudge_pitch_down", step => data.Angles[0] = NormalizeAngle(data.Angles[0] - step)))
            .Add(MakeNudgeButton("menu.option.nudge_yaw_left", step => data.Angles[1] = NormalizeAngle(data.Angles[1] - step)))
            .Add(MakeNudgeButton("menu.option.nudge_yaw_right", step => data.Angles[1] = NormalizeAngle(data.Angles[1] + step)))
            .Add(MakeNudgeButton("menu.option.nudge_roll_pos", step => data.Angles[2] = NormalizeAngle(data.Angles[2] + step)))
            .Add(MakeNudgeButton("menu.option.nudge_roll_neg", step => data.Angles[2] = NormalizeAngle(data.Angles[2] - step)))
            .Add(new ButtonComponent(T("menu.option.straighten"), ctx =>
            {
                float absRoll = MathF.Abs(data.Angles[2]);
                data.Angles[0] = 0f;
                data.Angles[2] = absRoll < 45f ? 0f : absRoll > 135f ? 180f : 90f;
                Save();
                UpdateLiveEntity(data);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, menu);
    }

    // ============================== 7. RENK SEÇİMİ ==============================

    private IMenu BuildColorMenu(int ownerSlot, WallTextData data)
    {
        bool syncing = false;
        SwatchColorSelector? swatchSelector = null;
        InputComponent? hexInput = null;
        ToggleComponent? rainbowToggle = null;

        int FindPresetIndex(string hex)
        {
            var normalized = NormalizeHex(hex);
            return Array.FindIndex(ColorPresets, c => c.Hex.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }

        int initialIdx = Math.Max(0, FindPresetIndex(data.ColorHex));

        swatchSelector = new SwatchColorSelector(
            "Renk Paleti",
            ColorPresets,
            initialIdx,
            change =>
            {
                if (syncing) return;
                syncing = true;
                data.ColorHex = change.NewValue.Hex;
                data.Rainbow = false;
                Save();
                UpdateLiveEntity(data);
                hexInput?.SetValue(change.Session, data.ColorHex);
                rainbowToggle?.SetValue(change.Session, false);
                syncing = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.color_changed", data.ColorHex)}");
            },
            preset => T(preset.TranslationKey))
        {
            DefaultValueProvider = _ => Math.Max(0, FindPresetIndex(data.ColorHex))
        };

        hexInput = new InputComponent(
            "Özel HEX Kodu",
            defaultValue: NormalizeHex(data.ColorHex),
            validator: IsValidHex,
            onChanged: change =>
            {
                if (syncing) return;
                syncing = true;
                data.ColorHex = NormalizeHex(change.NewValue);
                data.Rainbow = false;
                Save();
                UpdateLiveEntity(data);
                int matchIdx = FindPresetIndex(data.ColorHex);
                if (matchIdx >= 0)
                    swatchSelector?.SetValue(change.Session, matchIdx);
                hexInput?.SetValue(change.Session, data.ColorHex);
                rainbowToggle?.SetValue(change.Session, false);
                syncing = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.color_changed", data.ColorHex)}");
            })
        {
            MaxLength = 16,
            EmptyText = "#FFFFFF",
            InvalidText = "Geçersiz HEX formatı (#RRGGBB)",
            DefaultValueProvider = _ => NormalizeHex(data.ColorHex),
            HintMessage = "[gold]HEX renk kodunu sohbete yazın[default] (örn: [lime]#FF5500[default])"
        };

        rainbowToggle = new ToggleComponent("Rainbow (RGB) Modu", data.Rainbow, change =>
        {
            if (syncing) return;
            data.Rainbow = change.NewValue;
            Save();
            UpdateLiveEntity(data);
            change.Session.Invalidate();
        })
        {
            DefaultValueProvider = _ => data.Rainbow
        };

        var menu = _core.Menu.CreateMenu("cs2walltext.color")
            .WithTitle(SafePreview($"RENK SEÇİMİ: {data.Name}", 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Mevcut: {(data.Rainbow ? "Rainbow (RGB)" : NormalizeHex(data.ColorHex))}"
            })
            .Add(swatchSelector)
            .Add(hexInput)
            .Add(rainbowToggle)
            .Build();

        return Track(ownerSlot, menu);
    }

    // ============================== 8. DÖNEN DUYURU PANOSU ==============================

    private IMenu BuildRotatingMenu(int ownerSlot, WallTextData data)
    {
        var page = new PageState { RowsAt = 4 };
        IMenu? rotatingMenu = null;
        bool syncingAdd = false;
        InputComponent? addInput = null;
        void Populate()
        {
            if (rotatingMenu == null) return;
            var rows = new List<IMenuComponent>();
            for (int i = 0; i < data.RotatingMessages.Count; i++)
            {
                int index = i;
                string preview = SafePreview(data.RotatingMessages[index], 22);
                rows.Add(new DangerButtonComponent($"#{index + 1}: {preview}\t[Sil]", ctx =>
                {
                    if (index >= 0 && index < data.RotatingMessages.Count)
                        data.RotatingMessages.RemoveAt(index);
                    if (data.RotatingMessages.Count == 0)
                        data.IsRotating = false;
                    Save();
                    UpdateLiveEntity(data);
                    Populate();
                    ctx.Session.Invalidate();
                }));
            }

            if (rows.Count == 0)
            {
                rows.Add(new TextComponent("Henüz dönüşümlü mesaj eklenmedi"));
            }

            ReplaceRows(rotatingMenu, page, rows);
        }

        addInput = new InputComponent(
            "Yeni Duyuru Ekle",
            defaultValue: "",
            validator: v => !string.IsNullOrWhiteSpace(v),
            onChanged: change =>
            {
                if (syncingAdd) return;
                syncingAdd = true;
                string text = change.NewValue.Trim().Replace("\\n", "\n");
                addInput?.SetValue(change.Session, "");
                syncingAdd = false;

                data.RotatingMessages.Add(text);
                data.IsRotating = true;
                Save();
                UpdateLiveEntity(data);
                Populate();
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.rotating_message_added", text)}");
            })
        {
            MaxLength = 256,
            EmptyText = "Mesaj Yaz",
            InvalidText = "Mesaj boş olamaz",
            HintMessage = "[gold]Dönen panoya eklenecek mesajı yazın[default] (alt satır: [lime]\\n[default])"
        };

        var builder = _core.Menu.CreateMenu("cs2walltext.rotating")
            .WithTitle(SafePreview(T("menu.title.rotating", data.Name), 32))
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Kayıtlı: {data.RotatingMessages.Count} • Durum: {(data.IsRotating ? "AKTİF" : "KAPALI")}"
            })
            .Add(new ToggleComponent(T("menu.option.rotating_toggle"), data.IsRotating, change =>
            {
                data.IsRotating = change.NewValue;
                Save();
                UpdateLiveEntity(data);
                change.Session.Invalidate();
            })
            {
                DefaultValueProvider = _ => data.IsRotating
            })
            .Add(new SliderComponent(
                "Değişim Süresi",
                5f,
                120f,
                Math.Clamp(data.RotationIntervalSeconds, 5f, 120f),
                5f,
                change =>
                {
                    data.RotationIntervalSeconds = MathF.Round(change.NewValue);
                    Save();
                })
            {
                TotalBars = 10,
                ValueFormat = "0 sn",
                DefaultValueProvider = _ => Math.Clamp(data.RotationIntervalSeconds, 5f, 120f)
            })
            .Add(addInput)
            .Add(new DangerButtonComponent(T("menu.option.clear_rotating_msgs"), ctx =>
            {
                data.RotatingMessages.Clear();
                data.IsRotating = false;
                Save();
                UpdateLiveEntity(data);
                SendColoredChat(ctx.Player, $"{Prefix()} {T("chat.rotating_messages_cleared", data.Name)}");
                Populate();
                ctx.Session.Invalidate();
            })
            {
                VisibleWhen = _ => data.RotatingMessages.Count > 0
            });

        rotatingMenu = Track(ownerSlot, builder.Build());

        rotatingMenu.Opened += session =>
        {
            Populate();
            RestoreFocus(session, page);
        };

        rotatingMenu.FocusChanged += session =>
        {
            page.Focus = session.FocusedIndex;
        };

        return rotatingMenu;
    }

    // ============================== 9. ETKİLEŞİMLİ BUTON ==============================

    private IMenu BuildInteractMenu(int ownerSlot, WallTextData data)
    {
        string[] triggerTypes = ["both", "use", "shoot"];
        int trigIdx = Array.FindIndex(triggerTypes, t => t.Equals(data.TriggerType, StringComparison.OrdinalIgnoreCase));
        if (trigIdx < 0) trigIdx = 0;

        bool syncingMsg = false;
        bool syncingCmd = false;
        InputComponent? msgInput = null;
        InputComponent? cmdInput = null;

        msgInput = new InputComponent(
            "Oyuncuya Mesaj (- sil)",
            defaultValue: SafePreview(data.MessageToPlayer, 22),
            onChanged: change =>
            {
                if (syncingMsg) return;
                syncingMsg = true;
                var val = change.NewValue.Trim();
                data.MessageToPlayer = val == "-" ? "" : change.NewValue.Replace("\\n", "\n");
                if (!string.IsNullOrWhiteSpace(data.MessageToPlayer))
                    data.IsInteractable = true;
                Save();
                msgInput?.SetValue(change.Session, SafePreview(data.MessageToPlayer, 22));
                syncingMsg = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.interact_msg_set", data.MessageToPlayer)}");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(data.MessageToPlayer, 22),
            HintMessage = "[gold]Tetiklendiğinde oyuncuya gösterilecek mesajı yazın[default] (silmek için [lightred]-[default])"
        };

        cmdInput = new InputComponent(
            "Çalışacak Komut (- sil)",
            defaultValue: SafePreview(data.CommandToRun, 22),
            onChanged: change =>
            {
                if (syncingCmd) return;
                syncingCmd = true;
                var val = change.NewValue.Trim();
                data.CommandToRun = val == "-" ? "" : val;
                if (!string.IsNullOrWhiteSpace(data.CommandToRun))
                    data.IsInteractable = true;
                Save();
                cmdInput?.SetValue(change.Session, SafePreview(data.CommandToRun, 22));
                syncingCmd = false;
                change.Session.Invalidate();
                SendColoredChat(change.Player, $"{Prefix()} {T("chat.interact_cmd_set", data.CommandToRun)}");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(data.CommandToRun, 22),
            HintMessage = "[gold]Çalışacak komutu yazın[default] (örn: [lime]!vip[default] veya [lime]server: ...[default], silmek için [lightred]-[default])"
        };

        var menu = _core.Menu.CreateMenu("cs2walltext.interact")
            .WithTitle(SafePreview(T("menu.title.interact", data.Name), 32))
            .AddHeader(new TextComponent("+USE (E) veya Ateş Tetikleyici"))
            .Add(new ToggleComponent(T("menu.option.interact_toggle"), data.IsInteractable, change =>
            {
                data.IsInteractable = change.NewValue;
                Save();
                change.Session.Invalidate();
            })
            {
                DefaultValueProvider = _ => data.IsInteractable
            })
            .Add(new SelectorComponent<string>(
                "Tetikleyici",
                triggerTypes,
                trigIdx,
                change =>
                {
                    data.TriggerType = change.NewValue;
                    Save();
                })
            {
                DefaultValueProvider = _ =>
                {
                    int idx = Array.FindIndex(triggerTypes, t => t.Equals(data.TriggerType, StringComparison.OrdinalIgnoreCase));
                    return idx >= 0 ? idx : 0;
                },
                Formatter = t => t switch
                {
                    "use" => T("trigger.type.use"),
                    "shoot" => T("trigger.type.shoot"),
                    _ => T("trigger.type.both")
                }
            })
            .Add(msgInput)
            .Add(cmdInput)
            .Add(new SliderComponent(
                "Maks Mesafe",
                50f,
                1000f,
                Math.Clamp(data.InteractionDistance, 50f, 1000f),
                50f,
                change =>
                {
                    data.InteractionDistance = MathF.Round(change.NewValue);
                    Save();
                })
            {
                TotalBars = 10,
                ValueFormat = "0 br",
                DefaultValueProvider = _ => Math.Clamp(data.InteractionDistance, 50f, 1000f)
            })
            .Add(new SliderComponent(
                "Bekleme Süresi",
                0.5f,
                30f,
                Math.Clamp(data.CooldownSeconds, 0.5f, 30f),
                0.5f,
                change =>
                {
                    data.CooldownSeconds = MathF.Round(change.NewValue * 10f) / 10f;
                    Save();
                })
            {
                TotalBars = 10,
                ValueFormat = "0.0 sn",
                DefaultValueProvider = _ => Math.Clamp(data.CooldownSeconds, 0.5f, 30f)
            })
            .Build();

        return Track(ownerSlot, menu);
    }
}

