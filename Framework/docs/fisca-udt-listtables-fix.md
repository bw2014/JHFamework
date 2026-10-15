# FISCA.UDT 修正說明：啟動時重複呼叫 `UDTService.DDL.ListTables`

- 對象：維護 `kernel\FISCA.UDT.dll` 的開發者
- 版本：FISCA.UDT 1.0.2.4（2026-10-14 建置）
- 日期：2026-10-15

---

## 問題

高雄國中測試宿主（`E:\ischool-installer\1campus_desktop_高雄國中加速`）的啟動 log（`StartupTiming.log`）顯示：

- 主畫面可以操作之前，`UDTService.DDL.ListTables` 被呼叫了 **10 次**。每次請求 1.0 KB、回應 232.7 KB，伺服器處理 1.9～5.1 秒，**合計約 39 秒**。
- 呼叫來源分別是 10 個不同的模組。`KHJHGraduateSurvey` 是在 UI 執行緒上呼叫的，直接讓畫面卡住約 4.5 秒。
- 多次呼叫同時進行時會互相拖慢：單獨一次約 2 秒，3～4 次同時呼叫時，每次約 5 秒。
- 這 10 次都是在**快取還是空的時候**發出的，之後也沒有任何 DDL 呼叫。也就是說，每個模組都等於重新拿了一次一模一樣的清單。

| 開始時間 | 總耗時 | 伺服器 | 呼叫模組 |
|---|---|---|---|
| 16.3 s | 2.3 s | 1.9 s | K12.EduAdminDataMapping |
| 19.1 s | 3.5 s | 3.1 s | StudentTransferCoreImpl |
| 20.1 s | 3.0 s | 2.8 s | JHSchool.Association |
| 23.9 s | 2.3 s | 2.0 s | ClassLock_KH |
| 28.8 s | 4.8 s | 4.5 s | K12.Sports.FitnessImportExport |
| 28.9 s | 4.6 s | 4.2 s | K12EmergencyContact |
| 29.0 s | 5.3 s | 5.1 s | K12ScheduleManagement |
| 31.2 s | 4.5 s | 4.3 s | KH_Central.OfficeSystem |
| 31.2 s | 4.5 s | 4.2 s | KH_HighConcern |
| 31.2 s | 4.5 s | 4.2 s | KHJHGraduateSurvey（UI 執行緒） |

（2026-10-15 09:43 的啟動紀錄；所有呼叫的 via 都是 `FISCA.UDT.SchemaManager.GetTableElement`）

現行版本的快取 `_TablesCache` 是 `SchemaManager` 的成員變數，`SchemaManager` 又依 `DSConnection` 物件分開建立（`AccessHelper._SchemaManagers`）。照理說所有模組都用 `DSAServices.DefaultConnection`，會共用同一份快取（上表 10 個模組都只用 `new AccessHelper()`），但實際執行時有 3 次呼叫同時進行，而同一個 `SchemaManager` 抓取時是持有鎖定的，不可能同時進行。所以目前還不確定為什麼會有多份冷快取。

**下面的修正不依賴找出這個原因**：快取改成整個程序共用、以「主機加合約」為鍵，不管有幾個 `SchemaManager` 都只會抓一次。

---

## 修改 1：資料表清單改成整個程序共用，並且同時只抓一次

新增一個類別，取代 `SchemaManager` 的 `_TablesCache`、`_TablesCacheLock`、`GetTableElement`、`InvalidateTables`：

```csharp
/// <summary>
/// 全程序共用的資料表清單快取：同一主機加合約只呼叫一次 ListTables，同時要求的執行緒共用同一次結果。
/// </summary>
internal static class TableListCache
{
    private class Entry
    {
        public readonly object Lock = new object();
        public XmlElement Tables;   //null 代表尚未取得或已失效
        public int Version;         //每次重新取得就加 1
    }

    private static readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>();

    private static string KeyOf(DSConnection conn)
    {
        return conn.Connection.AccessPoint.Url + "|" + conn.Connection.TargetContract;
    }

    /// <summary>
    /// 取得指定資料表的描述（複本），找不到時傳回 null。version 傳回目前快取的版本。
    /// </summary>
    public static XmlElement GetTable(DSConnection conn, string tableName, out int version)
    {
        Entry entry = _entries.GetOrAdd(KeyOf(conn), key => new Entry());
        lock (entry.Lock)
        {
            if (entry.Tables == null)
            {
                entry.Tables = conn.CallService("UDTService.DDL.ListTables", new DSRequest()).GetContent().BaseElement;
                entry.Version++;
            }
            version = entry.Version;
            XmlElement table = entry.Tables.SelectSingleNode("Table[@Name='" + tableName.ToLower() + "']") as XmlElement;
            return table == null ? null : (XmlElement)table.CloneNode(true);
        }
    }

    /// <summary>
    /// 執行 DDL 後讓快取失效。若其他執行緒在 seenVersion 之後已經重新取得過，就不再清除，避免重複抓取。
    /// </summary>
    public static void Invalidate(DSConnection conn, int seenVersion)
    {
        Entry entry;
        if (!_entries.TryGetValue(KeyOf(conn), out entry))
            return;
        lock (entry.Lock)
        {
            if (entry.Version == seenVersion)
                entry.Tables = null;
        }
    }
}
```

- 鍵值用 `Connection.AccessPoint.Url` 加 `Connection.TargetContract`，不用物件本身。這兩個屬性都已經在宿主的 `FISCA.DSAClient` 確認存在。
- 抓取期間持有該鍵值的鎖定，所以 10 個模組同時要求時，實際只會呼叫一次 ListTables，其他的等這一次的結果。

---

## 修改 2：資料表不一致時，不要清掉整份清單重新抓

`SyncTable` 目前的流程是：

```csharp
XmlElement tableElement = GetTableElement(tableName, out var fresh);
if (!fresh && IsSchemaChangeRequired(tableElement, sampleRecord))
{
    InvalidateTables();                        //← 整份 232.7 KB 清單作廢
    tableElement = GetTableElement(tableName, out fresh);
}
```

只要有一個模組的資料表和伺服器不一致，整份清單就會作廢，下一個模組又得重抓。建議改成：

- **移除這段「不一致就先重抓」的流程。** 清單是這次執行中才剛抓的，不一致幾乎都表示這台用戶端真的需要執行 DDL，直接比對並執行 DDL 就好。
- **DDL 失敗時才重抓一次。** 例如另一台用戶端剛好先加了同一個欄位，這時呼叫 `TableListCache.Invalidate(conn, version)`，重新取得後再試一次；只重試一次。
- **DDL 成功後**呼叫 `TableListCache.Invalidate(conn, version)`。因為有版本保護，多個模組同時執行 DDL 時只會重抓一次。
- **更好的做法（選做）**：DDL 成功後直接修改快取中那一個 `Table` 元素（新增 `Field`、改 `Indexed` 屬性），完全不用重抓。

---

## 修改 3：統一判斷邏輯，修正 `bool.TryParse` 和 `bool.Parse` 不一致

現在有兩段邏輯在判斷同一件事，而且標準不一樣：

| 位置 | `Indexed` 屬性是空的或無效時 |
|---|---|
| `IsSchemaChangeRequired` | `bool.TryParse` 失敗，判定「需要修改」 |
| `SyncTable` | `bool.Parse` 直接丟出 `FormatException` |

結果是：只要某個欄位的 `Indexed` 屬性不正常，每次啟動都會被判定需要修改並觸發重抓，接著又丟出例外，下次啟動再重來一遍。

建議：

1. **抽出共用的解析方法**，兩處都改用它，缺少或無效時一律視為 `false`：

   ```csharp
   private static bool ReadIndexed(XmlElement field)
   {
       bool value;
       return bool.TryParse(field.GetAttribute("Indexed"), out value) && value;
   }
   ```

2. **把「需要哪些 DDL」整理成一個方法**，例如 `PlanChanges(tableElement, sampleRecord)`，回傳要執行的動作清單。`IsSchemaChangeRequired` 就等於「清單不是空的」。之後只有一份判斷邏輯，兩邊不會再各說各話。
3. **系統無法自動處理的不一致**（例如 Text 欄位要求建索引、Boolean 欄位要求建索引、欄位型別改變）：每次執行只記錄一次錯誤，不要觸發重抓。

---

## 修改 4（選做）：登入後在背景預先抓取

新增一個公開方法，例如 `AccessHelper.PrefetchSchemaAsync()`，在背景執行緒對 `DSAServices.DefaultConnection` 呼叫一次 `TableListCache.GetTable`。可以由最早啟動的模組（JHSchool 或 Framework 的 `Program.Initial`）呼叫。這樣第一個用到 UDT 的模組也不必等。

---

## 不在 FISCA.UDT 範圍內，但需要另外處理

`KHJHGraduateSurvey` 在 `Main` 裡、在 UI 執行緒上存取 UDT。即使上面都修好了，第一次存取仍可能要等一次 ListTables。建議把它啟動時的 UDT 存取移到背景執行緒，或延後到第一次真正用到時再做。

---

## 驗證方式

1. 準備量測版的 Framework.dll：記錄 `via` 的 log 探針已從 Framework 主線移除，請從 commit `3f68295` 建置一份含探針的 Framework.dll，只放在測試宿主的 `modules\JHCore\`（不要發布）。
2. 把新版 `FISCA.UDT.dll` 放進測試宿主的 `kernel\`，確認 `StartupTiming.enable` 還在，然後啟動、登入、等可以操作。
3. 打開 `StartupTiming.log` 最後一段的「查詢摘要」，找出 `schoolaccess via FISCA.UDT.…` 這一列。因為方法改名，via 會變成 `TableListCache.GetTable`。
   - **預期**：可以操作之前，ListTables 只有 **1 次**；修改前是 10 次，約 39 秒。
   - 如果還是多於 1 次，表示有不同的「主機加合約」組合。請看各次呼叫的端點（`schoolaccess`／`admin`）有沒有不同。
4. 冒煙測試：新增一個帶新欄位的 UDT 型別，確認欄位會自動建立；兩個模組同時 Insert 時，DDL 不會重複執行，也不會出錯。

---

## 風險

- **快取在整個程序的生命週期都有效。** 程式執行期間如果別的用戶端改了資料表結構，這台用戶端要到 DDL 失敗時才會發現（修改 2 的重試會處理）。目前的版本本來就有類似行為（快取到被清除為止）。
- **`_SyncedTypes` 的語意保持不變**：每個型別每次執行只同步一次。
