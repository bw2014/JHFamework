# Framework 模組載入速度改進執行計畫

- 分支：`fast-loading`
- 範圍：`Framework.dll`（JHCore 核心模組）的啟動流程 `Program.Initial()`，以及之後其他模組初始化時使用的組態快取（`ConfigurationManager` / `CacheManager<T>`）
- 日期：2026-10-15

---

## 1. 摘要

模組載入慢主要卡在網路等待，CPU 運算不是問題。`Program.Initial()` 在 UI 執行緒上依序發出至少 **5 個彼此無關的同步 DSA 呼叫**，總耗時大約是 5 次往返時間（RTT）相加。之後各模組讀取 `App/User/Global.Configuration[...]` 時，每個 namespace 又是一次同步往返，不存在的 namespace 還會**每次讀取都重新查詢**。

建議分四個階段處理：

| 階段 | 內容 | 風險 | 預期效果 |
|---|---|---|---|
| P0 | 建立量測基準 | 無 | 掌握實際瓶頸數據 |
| P1 | 啟動呼叫並行化、移除錯誤路徑上的同步阻塞 | 低 | 啟動網路時間從 ΣRTT 降到約 max(RTT) |
| P2 | 組態快取改善（負向快取、批次預取、共用快取） | 中 | 其他模組初始化時的往返次數大幅減少 |
| P3 | 延後載入、結構整理、建置設定 | 中 | 進一步縮短首屏時間，降低長期維護成本 |

---

## 2. 現況分析：啟動時發生了什麼

### 2.1 `Program.Initial()` 呼叫鏈（全部在 UI 執行緒、依序執行）

| # | 位置 | 呼叫 | 用途 | 是否必要於啟動當下 |
|---|---|---|---|---|
| 1 | `GlobalOld.BeginInitialize` → `Permission.GetUserAccessControlList` | `SmartSchool.Personal.GetRoles` | 使用者 ACL | 是（決定選單 Enable） |
| 2 | `new SchoolInformation()` | `SmartSchool.Config.GetSchoolInfo` | 學校名稱（視窗標題） | 只有標題用到 |
| 3 | `new SchoolConfig()` | `SmartSchool.Config.GetSchoolConfig` | 舊版組態（`[Obsolete]`） | **否**，僅 `ConfigurationCollection` 使用 |
| 4 | `new SystemConfig()` | `SmartSchool.Config.GetSystemConfig` | 學年度/學期（視窗標題） | 只有標題用到 |
| 5 | `new PreferenceProvider()` | `SmartSchool.Personal.GetPreference` | 個人偏好 | 可延後到第一次讀取 |
| 6 | `Campus.Configuration.Config.Initialize(...)` | 外部組件，行為待量測 | 第二套組態系統 | 待確認 |
| 7 | `RoleAclSource.Instance` → `FISCA.Permission.RoleAclSource.Instance` | 外部組件，行為待量測 | 權限目錄 | 待確認 |

第 1～5 項彼此沒有相依，卻依序執行。以每次 RTT 200 ms 計算，光這段就要約 1 秒；網路品質差的學校會放大好幾倍。

### 2.2 錯誤路徑會讓啟動卡更久

- `Legacy/SchoolConfig.cs` 的 `Load()` 失敗時**重試 5 次**，每次失敗都呼叫 `BugReporter.ReportException`。
- `BugReporter` 的建構子（`BugReporter.cs` 約第 108 行起）即使 `show == false` 也會：
  1. 建立 WinForm（`InitializeComponent()`）；
  2. 寫本機檔案；
  3. **同步**連線到 `http://beta.smartschool.com.tw/commonserver/quality` 並送出 `QualitySystem.ReportBug`。
- 結論：只要 SchoolConfig 服務異常，啟動時就可能在 UI 執行緒上依序經歷「5 次失敗呼叫 + 5 次對外回報連線（含逾時）」。`SchoolInformation`、`SystemConfig` 失敗時也會同步回報。

### 2.3 組態快取（`CacheManager<T>` / `ConfigurationCache`）

`ConfigurationManager` 建構時**不預載**資料，第一次讀取 `Configuration["ns"]` 會走到 `CacheItemCollection` 的 indexer（`CacheManager.cs:651`），快取未命中時同步呼叫 `SyncData(ID)`。問題如下：

| 問題 | 位置 | 影響 |
|---|---|---|
| 沒有負向快取：伺服器上不存在的 namespace，每次讀取都重新查詢 | `CacheManager.cs:661-667` | 常見寫法 `App.Configuration["X"]` 讀取尚未建立的組態時，每次都是一次往返 |
| 每個 namespace 單獨一次請求，沒有批次預取 API | `ConfigurationManager.this[]` | N 個模組各讀 M 個組態，就是 N×M 次往返 |
| 網路呼叫期間持有 `lock(_Parent._SortedOrder)` | `CacheManager.cs:655` | 所有讀取被序列化，背景執行緒與 UI 互相等待 |
| 更新單一 key 時會重建整個 `_List` 與 `_SortedOrder` | `CacheManager.cs:324-334` | O(N log N)，快取越大越慢 |
| 去重複用 `List.Contains` | `CacheManager.cs:189-193, 368-372` | O(n²)，大量 key 時明顯 |
| `Global.Configuration` 與 `Global.ConfigurationWritable` 各有一份快取與連線 | `Program.cs:26-28`、`ConfigProvider_Global` 建構子把 static `Connection` 設回 `null` | 同一份全域組態可能抓兩次，連線也可能被重建 |
| `SyncDataBackground` 在「全部 key 都已被移除」時直接 `return`，**沒有呼叫 `_Loading.Set()`** | `CacheManager.cs:385-386` | 之後所有 `Items` 存取永久卡在 `WaitOne()`，使用者看到的就是「一直在讀取」 |
| `SyncDataBackground` 在呼叫端（通常是 UI）先 `_Loading.WaitOne()` | `CacheManager.cs:365` | 若前一個背景同步還沒結束，UI 會被卡住 |

### 2.4 其他次要因素

- `BigEvent.Raise()` 使用 `Delegate.DynamicInvoke`（反射呼叫），所有快取事件都經過這裡；`UIRaise()` 使用同步 `Invoke`。
- `AsyncFieldLoader.Field_PreloadVariable` 每次都 `Console.WriteLine`。
- `ConfigProvider_Global` 第一次使用時才同步連線到 `quality` 主機（匿名、含 DNS 解析與連線建立）。
- `Framework.csproj`：
  - Debug 組態直接輸出到 `..\FiscaRuntime\addmodules\JHCore\`，且 `Optimize=false`。必須確認部署出去的是 Release 版。
  - `DevComponents.DotNetBar2`（4.6 MB）沒有設 `<Private>False</Private>`，會被複製到模組目錄。宿主程式已經載入這個組件，模組目錄多一份副本有機會造成重複探測或版本不一致；`app.config` 的 binding redirect（7.4.0.4）對 DLL 專案不會生效，且與參考版本 7.9.0.3 不一致。
  - `Security/UI/*`（RoleManager、UserManager 等）仍會編譯，但 `Program.cs` 已改用 `FISCA.Permission.UI`，屬於死碼，會增加組件大小。
  - `Resources/ischoolIcon.ico` 有 370 KB，啟動時就會載入。
- 外部依賴 `FISCA.Authentication.DSAServices.CallService` 的實作無法從原始碼看到。舊版（`DSAServices.cs` 中被註解掉的程式）**每次呼叫都會走訪整個 StackTrace 並反射讀取 Attribute**，需要用反編譯工具確認新版是否還這樣做。

---

## 3. 執行計畫

### P0：建立量測基準（0.5～1 天）

> 沒有基準就無法證明改善，也無法判斷 P3 是否值得做。

1. 在 `Program.Initial()` 每個步驟前後加上 `Stopwatch`，輸出到 `Trace`（或 `Application.StartupPath\StartupTiming.log`），至少涵蓋：
   - `AutoDisplayLoadingMessageOnMotherForm`
   - `Permission.GetUserAccessControlList`
   - `SchoolInformation` / `SchoolConfig` / `SystemConfig`
   - `PreferenceProvider`
   - 三個 `ConfigurationManager` 與 `DBChangeMonitor` 的建立
   - `Campus.Configuration.Config.Initialize`
   - `RoleAclSource.Instance` 首次存取
   - `GetTitleText`
2. 在 `ConfigurationCache.GetData` / `GetAllData` 加計數器，記錄啟動到主畫面可操作為止，**每個 namespace 被查詢的次數**，以及其中有多少是「伺服器不存在」的。
3. 用 Fiddler 或 Wireshark 錄一次完整啟動，統計 DSA 呼叫次數、各自耗時與 payload 大小。
4. 用 ILSpy/dnSpy 檢查 `FISCA.Authentication.dll` 中 `DSAServices.CallService` 的實作：
   - 是否每次呼叫都做 StackTrace 反射？
   - 是否支援多執行緒同時呼叫？（`ConfigProvider_*.SaveConfiguration` 已經用 3 條執行緒同時呼叫，推測可行，但要確認。）
5. 在兩種環境各量 5 次取中位數：(a) 內網低延遲；(b) 模擬學校網路（例如 clumsy 加 150 ms 延遲）。

**產出**：基準數據表，附在本文件第 5 節。

---

### P1：啟動流程快速改善（2～3 天，低風險）

#### P1-1 啟動呼叫並行化

把第 1～5 項（以及 P0 確認可並行的第 6、7 項）改成同時發出，全部完成後再繼續。專案目標框架是 .NET 4.0，可以用 TPL，但不能用 `async/await`。

```csharp
// Program.InitializeGlobalData() 改寫示意（.NET 4.0）
var tAcl    = Task.Factory.StartNew(() => Permission.GetUserAccessControlList());
var tInfo   = Task.Factory.StartNew(() => new SchoolInformation());
var tSys    = Task.Factory.StartNew(() => new SystemConfig());
var tPref   = Task.Factory.StartNew(() => Personal.GetPreference());   // 只抓資料，不建 Provider
Task.WaitAll(tAcl, tInfo, tSys, tPref);

GlobalOld.Acl = tAcl.Result;
GlobalOld.SchoolInformation = tInfo.Result;
GlobalOld.SystemConfig = tSys.Result;
var preference = new PreferenceProvider(tPref.Result);  // 新增 overload，在 UI 執行緒建立
```

注意事項：

- **`PreferenceProvider` 必須在 UI 執行緒建構**，因為它訂閱的 `Application.Idle` 只對建構時所在的執行緒有效。做法是在背景只抓 `DSResponse`，新增一個接收預先抓好的回應的建構子。
- `GlobalOld` 建構子會建立 `Control` 並取 `Handle`，必須保持在 UI 執行緒第一次存取。請在發出 Task **之前**先存取一次 `GlobalOld.MainThreadControl`。
- 例外處理：`Task.WaitAll` 會丟出 `AggregateException`。要攤平後沿用原本的錯誤行為（ACL 失敗必須中止，其他項目維持原本降級顯示「<讀取資訊錯誤>」）。
- 預期效果：啟動網路時間從約 5×RTT 降到約 1×RTT（取最慢的一個）。

#### P1-2 `SchoolConfig` / `ConfigurationCollection` 延後載入

兩者都標記 `[Obsolete]`，本專案內只有 `ConfigurationCollection` 會用到 `SchoolConfig`。

- 把 `GlobalOld.SchoolConfig` 與 `GlobalOld.Configuration` 改成**第一次存取時才建立**（lazy property），公開 API 簽章不變。
- **相容性風險**：其他模組可能直接讀 `GlobalOld.SchoolConfig`。lazy 後行為一致，只是第一次存取時才產生網路成本。發布前請在整個 JHFamework 方案內搜尋 `GlobalOld.SchoolConfig`、`GlobalOld.Configuration`、`Legacy.GlobalOld.Configuration`。

#### P1-3 錯誤回報不阻塞啟動

- `SchoolConfig.Load()`：重試改為最多 2 次，失敗只回報**一次**（目前每次重試都回報）。
- `BugReporter.ReportException(..., show: false, ...)`：
  - 不建立 Form，直接產生 XML；
  - 本機存檔後，用 `ThreadPool.QueueUserWorkItem` 在背景送出到回報伺服器；
  - `GetDSConnection()` 失敗後記住失敗狀態，在一段冷卻時間內不再重試。
- 同一個例外型別與訊息，在單次執行中只回報一次。

#### P1-4 確認部署 Release 版

- 確認實際部署到學校的 `Framework.dll` 是 Release 組態（`Optimize=true`）。
- Debug 的 `OutputPath` 指向執行目錄只適合本機開發，打包流程要明確使用 Release。

**P1 驗收**：同樣環境下，`Program.Initial()` 總耗時的中位數比 P0 基準至少下降 50%，且所有既有功能（選單權限、視窗標題、個人偏好儲存）行為不變。

---

### P2：組態快取改善（3～5 天，中風險）

#### P2-1 修正 `SyncDataBackground` 卡死問題（優先）

`CacheManager.cs:385-386` 提早 `return` 前補上 `_Loading.Set()`。這是正確性問題，但症狀就是「讀取中一直不結束」，因此列入本計畫，並最先處理。

#### P2-2 負向快取

- `CacheManager<T>` 新增 `HashSet<string> _MissingKeys`：`SyncData` 查不到的 key 記錄下來，`CacheItemCollection` 的 indexer 遇到這類 key 時直接回傳 `default(T)`，不再發請求。
- 以下情況要把 key 從 `_MissingKeys` 移除：
  - `ConfigData.Save()` 新增該 namespace 時；
  - 明確呼叫 `ConfigurationManager.Sync(ns)` 時；
  - `SyncAllBackground()` 時清空。
- 預期效果：「讀取未建立的組態」從每次一個往返變成整次執行只有一個。

#### P2-3 批次預取 API

```csharp
// ConfigurationManager 新增
public void Prefetch(params string[] configNamespaces);           // 一次請求抓多個 namespace
public void PrefetchBackground(params string[] configNamespaces); // 背景版本
```

- `GetConfiguration(IEnumerable<string>)` 本來就支援多個 `Condition/Name`，底層不需要修改。
- 啟動時在背景對 `User.Configuration` 呼叫 `SyncAllBackground()`：使用者組態通常很小，一次全抓比逐筆查詢划算。是否也對 `App.Configuration` 全抓，要依 P0 量到的資料量決定。
- 發文件通知其他模組：在各自的 `Main` / 初始化入口一次 `Prefetch` 所有會用到的 namespace。

#### P2-4 鎖與資料結構

- `CacheItemCollection` indexer：先在鎖內查詢，**釋放鎖之後**才發出網路請求，回來後再加鎖寫入（double-check）。
- `SyncData` / `SyncDataBackground` 的去重複改用 `HashSet<string>`。
- 更新少量 key 時，不再重建整個 `_List`：
  - 不可排序的型別（`ConfigurationRecord` 就是）直接對 Dictionary 新增、修改、刪除；
  - 可排序的型別才維護 `_SortedOrder`。
- 抽出 `SyncData` 與 `SyncDataBackground` 中重複約 150 行的合併邏輯，避免日後只修到其中一份。

#### P2-5 共用全域組態快取與連線

- `Global.Configuration`（唯讀）與 `Global.ConfigurationWritable` 改成共用同一個 `ConfigurationCache` 與 `ConfigProvider_Global` 實例，唯讀與否只在 `ConfigurationManager` 層判斷。
- `ConfigProvider_Global` 建構子不要再把 static `Connection` 設為 `null`。
- 啟動後在背景預先建立 `quality` 主機的連線（warm-up），避免第一次讀全域組態時在 UI 執行緒等待連線建立。

**P2 驗收**：P0 的「每個 namespace 查詢次數」統計中，同一個 namespace 查詢次數不超過 1 次（明確 `Sync` 除外）；新增單元測試覆蓋 `CacheManager` 的命中、未命中、負向快取、刪除與並行讀取。

---

### P3：延後載入與結構整理（視 P0～P2 結果決定，3～5 天）

| 項目 | 做法 | 備註 |
|---|---|---|
| 視窗標題非同步填入 | 先顯示「ischool 〈載入中…〉」，`SchoolInformation`/`SystemConfig` 完成後再更新 `MotherForm.Form.Text` | 讓這兩個呼叫完全離開關鍵路徑 |
| `PreferenceProvider` 延後 | 第一次 `this[key]` 時才抓資料（背景預取 + 等待） | 需確認宿主何時第一次讀 Preference |
| 兩套組態系統 | `Campus.Configuration.Config` 與本專案 `ConfigurationManager` 各自維護快取，同一個 namespace 可能被抓兩次。評估讓其中一套轉接到另一套 | 依 P0 量到的 `Config.Initialize` 成本決定優先度 |
| `BigEvent` | 對已知簽章（`EventHandler`、`EventHandler<T>`）改成直接轉型呼叫，取代 `DynamicInvoke`；`UIRaise` 在可行處改用 `BeginInvoke` | 改用 `BeginInvoke` 會改變事件同步語意，要逐一確認呼叫端 |
| `AsyncFieldLoader` | 移除 `Console.WriteLine`；`Field.Reload()` 做節流（debounce） | 清單畫面捲動時有感 |
| 參考整理 | `DevComponents.DotNetBar2` 加上 `<Private>False</Private>`；移除 `app.config` 中無效的 binding redirect | 縮小模組體積，避免組件版本衝突 |
| 死碼移除 | 從 csproj 移除 `Security/UI/*` 與未使用的資源（`寬箭頭*.png`、`紅箭頭.png`，只在被註解的程式中使用） | 先在方案內確認沒有外部模組引用這些 public 型別 |
| 圖示瘦身 | `ischoolIcon.ico` 只保留 16/32/48/256 尺寸 | 效益小，順手做 |
| 目標框架升級 | 升級到 .NET 4.8 | 不是 P1/P2 的前提；要先處理下游專案，詳見 3.1 |

#### 3.1 目標框架升級到 4.8 的評估（2026-10-15 試編譯結果）

**Framework 本身**

- 在暫存目錄把 `TargetFrameworkVersion` 從 v4.0 改成 v4.8 後，用 Release 編譯：0 個錯誤，警告與 v4.0 完全相同（24 個，CS0612/CS0618/CS0168，都是原本就有的）。
- 所有引用的 DLL 都是 CLR 2.0 或 4.0 的組件，在 4.8 下都能載入。
- 執行時的相容行為（例如 TLS 預設值、AppContext 開關）由**宿主程式**的目標框架決定，不看模組 DLL。只改 Framework 的目標框架，執行時行為基本不變。
- 升級後才能用的主要是 `async/await` 和 `Task.Run`。P1/P2 需要的 `Task.Factory.StartNew`、`Task.WaitAll`、`Lazy<T>`、`ConcurrentDictionary` 在 .NET 4.0 就有，**升級不是載入加速的前提**。

**下游專案（主要風險）**

`Modules` 底下引用 Framework.dll 的專案（排除 Backup 資料夾）共 66 個：

| 目標框架 | 數量 | 換上 4.8 版 Framework.dll 後 |
|---|---|---|
| v4.8 | 37（包含 JHSchool 核心） | 沒有影響 |
| v4.5 / 4.5.2 / 4.6 | 3 | 編譯失敗 |
| v4.0 | 24 | 編譯失敗 |
| v3.5 | 2 | 本來就不能引用 4.0 的 DLL，應該已經沒在維護 |

- **失敗方式**：MSBuild 發現 DLL 的目標框架比專案高時，會發出 MSB3274 警告並拿掉該引用，接著出現大量 `CS0246`。
- **觸發時機**：下游專案用 HintPath 引用自己的 `Library\Framework.dll` 複本。等到有人把新版 DLL 複製進該專案的 `Library\` 再編譯時才會出現。
- **已部署的模組不受影響**：執行時 CLR 不檢查目標框架。

**學校端電腦**：已經有 37 個以 4.8 為目標的模組在使用，Windows 10 1903 以後和 Windows 11 也內建 4.8，學校電腦很可能都已經有 4.8。宿主 FiscaRuntime 不在本工作區，它本身的目標框架還沒確認；若還有沒裝 4.8 的 Windows 7 學校，需要另外確認。

**建議順序**：

1. 先在 4.0 上完成 P0～P2。
2. 把那 27 個 v4.0～4.6 的下游專案改成 v4.8。
3. 最後發布 4.8 版的 Framework.dll。

---

## 4. 時程與順序

```text
第 1 週  P0 量測 ──► P1-1 並行化 ──► P1-2 延後載入 ──► P1-3 錯誤回報 ──► P1-4 Release 確認 ──► P1 驗收
第 2 週  P2-1 卡死修正 ──► P2-2 負向快取 ──► P2-4 鎖/資料結構 ──► P2-3 預取 API ──► P2-5 共用快取 ──► P2 驗收
第 3 週  依 P0～P2 數據挑選 P3 項目
```

每個子項目獨立 commit，方便二分搜尋（bisect）與回滾。

## 5. 量測結果紀錄（待填）

| 指標 | P0 基準 | P1 後 | P2 後 | P3 後 |
|---|---|---|---|---|
| `Program.Initial()` 總耗時（內網） | | | | |
| `Program.Initial()` 總耗時（+150 ms 延遲） | | | | |
| 啟動期間 DSA 呼叫次數 | | | | |
| 組態 namespace 重複查詢次數 | | | | |
| 從登入到主畫面可操作的時間 | | | | |

## 6. 風險與回滾

| 風險 | 緩解方式 |
|---|---|
| `DSAServices.CallService` 不支援並行呼叫 | P0 先反編譯確認；不支援時 P1-1 改成只把非 DSA 的部分並行，或在 Framework 內加連線池 |
| 背景執行緒觸碰 UI 物件 | 背景 Task 只做網路與 XML 解析；所有 `MotherForm`、`Control`、`Application.Idle` 操作留在 UI 執行緒 |
| 其他模組依賴「啟動時 `SchoolConfig` 已載入」的副作用 | lazy property 保持 API 不變；在 JHFamework 方案內全域搜尋使用處 |
| 負向快取讓「別處剛建立的組態」讀不到 | 提供 `Sync(ns)` 強制刷新；`DBChangeMonitor` 若日後實作組態表監聽，可同時清除負向快取 |
| 行為回歸 | 每階段完成後在測試學校環境做冒煙測試：登入、選單權限、使用者/角色管理、個人偏好儲存、全域組態讀寫 |

回滾：每個子項目是獨立 commit，出問題時 `git revert` 對應 commit 即可，不需要整個分支回退。

## 7. 執行紀錄：P0～P2（2026-10-15）

### 7.1 P0 量測

**如何開始量測**：在 FISCA 執行目錄（`Application.StartupPath`）放一個空檔案 `StartupTiming.enable`，重新啟動後會產生 `StartupTiming.log`，內容包括：
- `Program.Initial()` 每個步驟的耗時（並行呼叫會分別記錄各自的耗時與所在執行緒）；
- 每次組態查詢的 namespace 與結果（存在／不存在）；
- 程式結束時輸出「查詢次數摘要」，次數多的排前面。

沒有這個檔案時，量測程式只會檢查一次檔案是否存在，不會有其他額外成本。實作在 `StartupProfiler.cs`。

> **探針已移除**：下面的 log 探針（`StartupProbes.cs` 與其測試）完成量測任務後已從程式碼移除，`StartupProfiler` 的步驟計時與組態查詢紀錄保留。需要再次量測時，可以從 commit `3f68295` 取回 `Framework/StartupProbes.cs`、`Framework.Tests/StartupProbesTests.cs`，以及 `Program.Initial()` 開頭的 `StartupProbes.Install();`。

**Log 探針**（已移除；原本在 `Program.Initial()` 一開始安裝；Framework 是最早啟動的模組之一，之後載入的模組都量得到）：

| 探針 | 切入點 | log 內容 |
|---|---|---|
| ① 時間軸 | `Process.StartTime` | 每行開頭是「程序啟動後經過的毫秒數」；第一行記錄 Framework 載入時程序已經執行多久（登入、更新、核心啟動的總時間） |
| ② 組件載入 | `AppDomain.AssemblyLoad` | `組件載入 <名稱> <版本>`，略過 GAC 與動態組件 |
| ③ DSA 呼叫 | `Connection.DefaultHttpManager`、`NameResolver.DefaultHttpManager` 的 `TripCompleted` | `DSA <服務> 總 x ms / 伺服器 y ms / 送 / 收 ← <呼叫模組>`。「總 − 伺服器」大約是網路與傳輸時間；失敗的呼叫用開始時間推算耗時 |
| ④ 模組 Main 耗時 | `RTContext.Kernel/Modules[*].Startups[*].StartupElapsed`（宿主已經量好） | 依耗時排名，標示失敗的模組；在「可操作」與「程式結束」時各輸出一次 |
| ⑤ 可操作時間點 | 主視窗 `Shown` 之後第一次 `Application.Idle` | `可操作（主視窗顯示後第一次 Idle）` |
| ⑥ 查詢摘要 | 所有 DSA 呼叫與組態查詢 | 依累計耗時排序的「次數 \| 累計 ms \| 鍵值」 |

**判讀方式**：先看 ⑤ 的時間與第一行的「程序已執行」，確認慢在 Framework 之前還是之後；再看 ④ 找出最慢的模組；最後用 ③／⑥ 找出該模組最慢、或重複最多次的服務，判斷是網路慢、伺服器慢，還是呼叫次數太多。

**安全性**：③ 的事件由宿主在呼叫端執行緒同步引發，探針一律攔截所有例外，不會影響原本的呼叫（有測試驗證，連線失敗時傳回的仍是宿主原本的例外）。④、⑤ 用到宿主的公開 API，每個探針都獨立保護，宿主版本沒有這些成員時只會記錄一行說明（測試環境沒有 `FISCA.Core`，已驗證會這樣處理）。③ 每次呼叫都要走訪呼叫堆疊，只在啟用量測時才做。

**反編譯 `FISCA.Authentication.DSAServices.CallService` 的結果**：
- 沒有全域鎖定，`connection.SendRequest` 可以多執行緒同時呼叫。P1-1 的並行化可行。
- **每次呼叫**都會建立 `new StackTrace()`，並對每個堆疊框架的方法與類別呼叫 `GetCustomAttributes`，以判斷是否套用 `AutoRetryOnWebException`。這是每次呼叫都要付出的反射成本，但位於外部組件，Framework 改不到。
- `_RunningRequest++/--` 不是原子操作，並行呼叫時「載入中」指示的計數可能有小誤差（只影響顯示）。
- FISCA 宿主（`FISCA.dll`）與呼叫端 `JHSchool.Program.Main`（只有 `[ApplicationMain]`）都沒有套用重試相關的 Attribute，所以把呼叫移到背景執行緒後，自動重試的判斷結果不變。

**待辦**：第 5 節的數據必須在實際環境（含 FISCA 宿主與學校主機）量測，本機無法執行宿主程式。

### 7.2 P1 完成項目

| 項目 | 實作 |
|---|---|
| P1-1 並行化 | `Program.InitializeGlobalData`：ACL、SchoolInformation、SystemConfig、GetPreference 四個呼叫用 `Task.Factory.StartNew` 同時發出。先在 UI 執行緒建立 `GlobalOld.MainThreadControl`；`PreferenceProvider` 改為在 UI 執行緒用預先取得的回應建立（新增 internal 建構子）。任一失敗時，依原本的順序丟出第一個原始例外 |
| P1-2 延後載入 | `GlobalOld.SchoolConfig`、`GlobalOld.Configuration` 改為第一次存取時才建立（有鎖定），公開 API 不變。整個 `Modules` 目錄只有 Framework 內部使用這兩個屬性 |
| P1-3 錯誤回報 | `SchoolConfig.Load` 重試從 5 次改為 2 次，全部失敗才回報一次。`BugReporter` 不顯示畫面的回報（`show == false`）不再建立表單，本機存檔後在背景傳送，同一種錯誤每次執行只傳送一次。回報主機連線失敗後 5 分鐘內不再重試 |
| P1-4 Release | 程式碼沒有修改。部署前仍要確認打包流程使用 Release 組態 |

`Campus.Configuration.Config.Initialize` 與 `RoleAclSource` 維持依序執行，只加上量測，等 P0 數據出來再決定是否處理。

### 7.3 P2 完成項目

| 項目 | 實作 |
|---|---|
| P2-1 卡死修正 | `SyncDataBackground` 提早 return 前補上 `_Loading.Set()` |
| P2-2 負向快取 | `CacheManager<T>` 新增 `protected virtual bool CacheMissingKeys`（**預設 false**，其他模組繼承 `CacheManager<T>` 的類別行為不變），`ConfigurationCache` 覆寫為 true。明確呼叫 `SyncData`／`SyncDataBackground`／`SyncAllBackground` 會重新查詢並更新記錄，所以 `ConfigData.Save()` 之後可以讀到新組態 |
| P2-3 預取 API | `ConfigurationManager.Prefetch(...)`、`PrefetchBackground(...)`：一次請求取得多個 namespace，略過已快取或已確認不存在的 |
| P2-4 鎖與資料結構 | 索引子向主機查詢時不再持有鎖定；`BeginLoading()` 讓「等待並標示載入中」變成不可分割的操作；`_SortedOrder` 從 `SortedList`（每次插入 O(n)，重建 O(n²)）改成 `Dictionary`；去重複與比對改用 `HashSet`；`SyncData` 與 `SyncDataBackground` 重複約 150 行的合併邏輯抽成 `MergeData`；對 `_List` 的修改都在鎖定內進行 |
| P2-5 共用全域快取 | `Global.Configuration`（唯讀）與 `Global.ConfigurationWritable` 共用同一個快取與資料來源；`ConfigProvider_Global` 建構子不再把連線清掉，連線建立加上鎖定；啟動後在背景預先建立連線（`WarmUp`） |

**與計畫不同的地方**：
- **沒有在啟動時對 `User.Configuration` 呼叫 `SyncAllBackground()`**。原因有二：使用者組態的資料量還沒量測；`SyncAllBackground` 失敗時會在 UI 執行緒的 `RunWorkerCompleted` 直接丟出例外，放在啟動流程中風險太高。等 P0 數據出來再決定，或改由各模組呼叫 `Prefetch`。
- **沒有改成「只增量更新」的寫法**：為了維持不可排序型別「被更新的項目移到最後」的既有順序語意，`MergeData` 仍然重建 `_List`。實際成本已經由 `_SortedOrder` 改成 `Dictionary` 從 O(n²) 降到 O(n)。
- 索引子釋放鎖定的效益有限：`CacheManager.this[]` 與 `Items` 在同步進行中仍會先等待 `_Loading`（原本的設計）。受益的是已經拿到 `Items` 參考的呼叫端。
- `SyncDataBackground` 在呼叫端執行緒等待前一次同步完成的問題沒有處理，改動需要佇列化，留待之後評估。

### 7.4 驗證

- **編譯**：Release 組態 0 個錯誤。警告只剩引用 `[Obsolete]` 舊類別造成的 CS0612/CS0618（23 個，修改前 23 個；因為使用位置移動，兩種代碼的個別數量有增減）。原本的 CS0168 因移除未使用的變數而消失，沒有新增其他種類的警告。
- **測試**：新增 `Framework.Tests`（主控台程式，不需要 NuGet 套件），共 22 個測試，涵蓋快取命中、未命中、負向快取、刪除、排序、背景同步、P2-1 卡死、多執行緒同步、`ConfigurationManager` 的負向快取、Prefetch、儲存後可讀取，以及 log 探針（封包解析、呼叫來源、排名、組件載入、實際 HTTP 失敗時的紀錄與例外不變）。
  - 新版：全部通過（快取的 15 個連續 3 次，加入探針後的 22 個連續 2 次）。
  - 探針移除後，探針相關的測試一併移除，保留快取與組態的 15 個測試。
  - Code review 修正兩個問題，並各補上測試（共 18 個）：
    - **使用者組態的負向快取無法以 `Sync()` 更新**：`ConfigurationManager.Sync()` 沒有套用 `NamespacePreprocess()`，使用者組態的快取鍵值是「帳號::名稱」，`Sync("名稱")` 卻同步了「名稱」，導致曾被記為不存在的使用者組態，在伺服器新增後仍讀不到。已改為套用前綴。
    - **並行讀取同一筆資料會重複查詢**：索引子未命中後呼叫 `SyncData()`，等待其他執行緒的載入完成後沒有再確認快取，兩個執行緒同時讀同一筆會各查一次（master 因為查詢時持有鎖定，只查一次）。索引子改用 `SyncMissingKey()`，取得載入權後若已載入或已確認不存在就不再查詢；明確呼叫 `SyncData()` 仍一律重新查詢。
    - 三個新測試對修正前的版本執行都會失敗（並行讀取查詢 2 次、`Sync` 使用未加前綴的名稱），修正後全部通過。
  - 用同一份測試對**修改前**的 `Framework.dll` 執行：負向快取測試失敗（功能不存在），P2-1 卡死測試失敗（證實原本會卡死），多執行緒同步測試**讓整個程序崩潰**（連續 3 次都一樣，原因是原本的 `WaitOne/Reset` 有競爭條件）。
- **執行測試**：

  ```text
  MSBuild Framework.Tests\Framework.Tests.csproj
  Framework.Tests\bin\Release\Framework.Tests.exe   （結束代碼 = 失敗數）
  ```

  測試專案在 `..\LibraryProject` 不存在時，會自動改用 `Framework\Library` 解析參考，Framework 一律以 Release 組態建置（Debug 組態會輸出到 FiscaRuntime）。
- **還沒驗證**：在 FISCA 宿主中實際啟動。請在測試學校環境進行冒煙測試（登入、選單權限、使用者/角色管理、個人偏好儲存、全域組態讀寫），並用 `StartupTiming.enable` 量測，填入第 5 節。

## 附錄：審查時順帶發現的非效能問題

- `Security/FeatureAcl.cs` indexer 用 `featureCode.Trim()` 檢查 `ContainsKey`，卻用未 Trim 的 `featureCode` 取值，前後有空白時會丟 `KeyNotFoundException`。
- `CacheManager.SyncData` 使用 `throw ex;`，會遺失原始堆疊，應改為 `throw;`。
- `PasswordHash.Compute` 使用 SHA1 且沒有加鹽，屬於安全性議題（與伺服器協定綁定，不在本計畫範圍內）。
