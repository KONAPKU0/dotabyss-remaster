# AbyssMod 中日名稱相容層

`AbyssSniff.LocalizationCompat.dll` 是 `AbyssSniff` 的附加 BepInEx 插件。它不取代或修改原本沒有公開源碼的 `AbyssSniff.dll`，只在原插件內部進行文字判定前建立中日名稱別名。

本 fork 現在直接使用上游完整 **v1.6.1**，不再套用 Automation Only 修改；戰鬥功能、`character_fixes_enabled` 與 `F4` 恢復上游行為。移除的精簡工具仍可從 Git 歷史找回。

上游基準：[65380b0](https://github.com/DotGoodGirl114514/dotabyss-remaster/commit/65380b06f2e3a93d52b20ac946d044e81675bbc5)。原版 `AbyssSniff.dll` 的 SHA-256 為 `64AD4F817374111ADBB0D75E3A60E75E3000D59DFC02DCD69ACBDCDE53FE27F6`；靜態測試會檢查它逐位元組保持原樣。後續同步若更換原版 DLL，必須重新確認方法簽名及雜湊，不能只改版本字串。

## 資料來源與行為

- 名稱與介面別名僅讀取本機 `BepInEx/plugins/AbyssMod/cache/*/static.json` 與 `ui_texts.json`；另外內建四個 AbyssSniff 必需的 Code 類別中日別名。
- 不連線、不呼叫翻譯服務，也不寫入 AbyssMod 或漢化檔案。
- 快取在遊戲啟動後才產生或更新也可以；插件會定期檢查檔案時間與大小，變更後重新載入。
- 快取不存在、JSON 損壞或譯名重複時會安全跳過。沒有安裝或關閉漢化時，原本的日文流程不變。
- 不新增 `reroll_config.json` 欄位。`force_chain_allow_names` 可繼續填日文，也可以填中文。

## 相容範圍

- 深淵 Code 顯示名稱會在 AbyssSniff 查詢日文 MasterData 前轉回原文，恢復候選分類、選擇、領取與後續流程。
- 深淵類別的「ラッシュ／インパクト／セーフ／リスク」與「衝鋒／衝擊／安全／風險」可互相識別。
- AbyssSniff 讀取的決定、確認、取消、快速選擇等按鈕文字會接受翻譯前後的別名；玩家畫面仍保持中文。
- 自動水晶白名單使用雙向別名子字串比對，例如日文設定 `情熱のマナクリスタル` 可命中 `热情能量水晶【刚力】`，中文設定也可命中日文完整名稱。

## 建置與檢查

需要 .NET SDK `6.0.428`：

```powershell
dotnet build src/AbyssSniff.LocalizationCompat/AbyssSniff.LocalizationCompat.csproj --configuration Release
dotnet run --project tests/AbyssSniff.LocalizationCompat.Tests/AbyssSniff.LocalizationCompat.Tests.csproj --configuration Release -- $PWD
```

Release 建置只編譯相容插件，會把 DLL 複製到 `BepInEx/plugins/AbyssSniff/AbyssSniff.LocalizationCompat.dll`；原版 DLL 沒有公開源码，直接採用上游成品，不重新編譯或修改。測試會同時檢查別名、快取重載、損壞資料的 fail-open 行為、原版 DLL 雜湊、補丁目標簽名與 Harmony 參數名稱，以及 Release 插件中繼資料與安裝目錄。

GitHub Actions 的 `AbyssSniff-v1.6.1-with-localization` 是**舊版升級包**，包含兩個 DLL、上游 `BepInEx.cfg` 和安裝說明，不包含個人 `reroll_config.json` 或漢化快取；全新安裝應下載本 fork 合併後的完整 `main`。上游一次性 Release 發布工作流只允許在上游倉庫執行，避免本 fork 自動發布缺少漢化兼容的舊標籤。

## 故障排查

1. 先依 README 的升級步驟把舊 `AbyssSniff` 資料夾移到 `BepInEx` 以外備份，再安裝本 fork 的兩個 DLL；不能還原 `Project.dll` 等舊 interop DLL 或舊 `Release` 子目錄。
2. 還原個人 `reroll_config.json` 及 `data` 內設定檔即可，不要還原診斷 marker。`BepInEx/plugins/AbyssMod` 和其快取不需搬動或修改。
3. v1.6.1 預設關閉磁碟日誌，沒有 `LogOutput.log` 不代表相容插件未載入。排查時關閉遊戲，在 `BepInEx/config/BepInEx.cfg` 的 `[Logging.Disk]` 將 `Enabled = true`，重新啟動後在 `BepInEx/LogOutput.log` 搜尋 `[LocalizationCompat] ready` 及 `target ... missing/mismatch`。排查完成後恢復 `Enabled = false`。
4. 核對 `BepInEx/plugins/AbyssMod/cache/*/static.json` 和 `ui_texts.json` 已生成且為有效 JSON。相容插件只讀取本機快取；不需要改動漢化檔案或中文界面。

若仍出現無法施放技能，這個版本保留上游戰鬥功能；名稱相容層不修正遊戲技能邏輯，需另行根據遊戲版本與日誌診斷，不能把構建成功視為已修復該問題。

## 遊戲內驗收清單

- 開啟簡體中文漢化後，自動深淵可讀取候選 Code、完成選擇與領取，並繼續下一步。
- 中文水晶名稱可命中現有日文 `force_chain_allow_names`；把設定改成中文後也可命中日文名稱。
- 決定、確認、取消與快速選擇等自動按鈕流程可繼續執行。
- 關閉漢化後，以相同設定重跑上述流程，日文狀態仍正常。
- 新版遊戲中可正常進入深淵及選擇樓層；掉落條件不符時自動刷裝備，達標時正常保留。
- 手動點水晶與自動水晶都能施放；戰鬥行為按上游設定執行。
- 必要時依故障排查步驟暫時開啟日誌，確認 `[LocalizationCompat] ready`；若上游方法簽名改變，只會記錄 `target ... missing/mismatch` 警告，不會阻止遊戲啟動。

目前環境沒有遊戲安裝目錄，只能完成構建、單元與靜態檢查；以上遊戲內項目仍需使用者實測。
