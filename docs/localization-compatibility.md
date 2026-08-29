# AbyssMod 中日名稱相容層

`AbyssSniff.LocalizationCompat.dll` 是 `AbyssSniff` 的附加 BepInEx 插件。它不取代或修改原本沒有公開源碼的 `AbyssSniff.dll`，只在原插件內部進行文字判定前建立中日名稱別名。

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

Release 建置會把 DLL 複製到 `BepInEx/plugins/AbyssSniff/AbyssSniff.LocalizationCompat.dll`。測試會同時檢查別名、快取重載、損壞資料的 fail-open 行為、原版 DLL 補丁目標簽名及 Release 插件中繼資料。

## 遊戲內驗收清單

- 開啟簡體中文漢化後，自動深淵可讀取候選 Code、完成選擇與領取，並繼續下一步。
- 中文水晶名稱可命中現有日文 `force_chain_allow_names`；把設定改成中文後也可命中日文名稱。
- 決定、確認、取消與快速選擇等自動按鈕流程可繼續執行。
- 關閉漢化後，以相同設定重跑上述流程，日文狀態仍正常。
- `BepInEx/LogOutput.log` 中出現 `[LocalizationCompat] ready`；若上游方法簽名改變，只會記錄 `target ... missing/mismatch` 警告，不會阻止遊戲啟動。
