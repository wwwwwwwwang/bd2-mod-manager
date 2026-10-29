using System;
using System.Collections;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// 假游戏结构。命名空间、类型名、字段布局都按真实客户端的样子摆，
// 目的是让插件的反射层在不依赖游戏本体的前提下走完整条逻辑。
// ---------------------------------------------------------------------------

namespace Proto.Net
{
    public class CostumeDBInfo
    {
        public long Id { get; set; }
        public int DesignId { get; set; }

        /// <summary>玩家实际买过的皮肤（服务端下发的那份）。</summary>
        public HashSet<int> OwnedDesigns { get; } = new HashSet<int>();

        // 原版行为：只返回「已经拥有的皮肤」
        public List<PrestigeSkinDBInfo> GetConnectedPrestigeSkinDBInfos()
            => FakeGame.Database.ConnectedSkins(this);
    }

    public class PrestigeSkinDBInfo
    {
        public int CostumeId { get; set; }
        public int CostumeDesignId { get; set; }
        public bool IsSet { get; set; }
    }

    /// <summary>道具（大厅互动场景的载体）。Type=25 是皮肤场景。</summary>
    public class ItemDBInfo
    {
        public int Id { get; set; }
        public bool IsNew { get; set; }
        public int Type { get; set; }
    }
}

namespace FakeGame
{
    using Proto.Net;

    /// <summary>皮肤交换弹窗。贴图是异步的：没资源时回调可能永远不来。</summary>
    public class SkinExchangePopupUI
    {
        public static Action<Sprite> LastCallback;
        public static int Calls;

        public void LoadTexture(int designId, ref Action<Sprite> callback)
        {
            Calls++;
            LastCallback = callback;   // 「资源还没到，先把回调存着」
        }

        public static void Complete(Sprite sprite)
        {
            var cb = LastCallback;
            LastCallback = null;
            cb?.Invoke(sprite);
        }

        public static void Reset()
        {
            LastCallback = null;
            Calls = 0;
        }
    }
}

namespace FakeGame
{
    /// <summary>占位的 Sprite。插件不引用 UnityEngine，委托参数类型是运行期取的，随便给个类即可。</summary>
    public class Sprite
    {
        public string Tag = "sprite";
    }
}

namespace Proto.Design.common
{
    public class PrestigeSkinTable
    {
        public int Id { get; set; }
        public int CostumeId { get; set; }

        public static PrestigeSkinTable GetPrestigeSkinTable(int designId)
            => FakeGame.Database.FindRow(designId);
    }

    /// <summary>大厅设定表：PrestigeSkinId != 0 的项就是「皮肤互动场景」。</summary>
    public class LobbySettingItemTable
    {
        public int Id { get; set; }
        public int PrestigeSkinId { get; set; }
        public int Type { get; set; }

        public static List<LobbySettingItemTable> GetAllLobbySettingItems()
            => FakeGame.Database.LobbyItems;
    }
}

namespace FakeGame
{
    using Proto.Design.common;
    using Proto.Net;

    public enum TableKind
    {
        DB_COMMON,
        DB_OTHER,
    }

    public class RawDataManager
    {
        public static RawDataManager Instance { get; } = new RawDataManager();

        public IList GetTableOfIds(TableKind kind, string tableName)
        {
            if (kind != TableKind.DB_COMMON || tableName != "PrestigeSkinTable")
                return new List<int>();

            return new List<int>(Database.DesignIds);
        }
    }

    public static class Database
    {
        public static readonly Dictionary<long, CostumeDBInfo> CostumeDict =
            new Dictionary<long, CostumeDBInfo>();

        public static readonly Dictionary<int, PrestigeSkinDBInfo> SkinDict =
            new Dictionary<int, PrestigeSkinDBInfo>();

        /// <summary>道具字典。宿主类上还要有一个 List&lt;ItemDBInfo&gt; 静态属性，才认得出这是道具管理类。</summary>
        public static readonly Dictionary<int, ItemDBInfo> ItemDict =
            new Dictionary<int, ItemDBInfo>();

        public static List<ItemDBInfo> Items { get; } = new List<ItemDBInfo>();

        public static List<LobbySettingItemTable> LobbyItems { get; } = new List<LobbySettingItemTable>();

        private static readonly Dictionary<int, PrestigeSkinTable> Rows =
            new Dictionary<int, PrestigeSkinTable>();

        public static List<int> DesignIds { get; } = new List<int>();

        /// <summary>原方法被真正执行的次数。补丁生效时它应该保持 0。</summary>
        public static int SetSkinCalls;

        public static void Reset()
        {
            CostumeDict.Clear();
            SkinDict.Clear();
            ItemDict.Clear();
            Items.Clear();
            LobbyItems.Clear();
            SkinExchangePopupUI.Reset();
            Rows.Clear();
            DesignIds.Clear();
            SetSkinCalls = 0;
        }

        public static void AddSkinRow(int designId, int costumeId)
        {
            Rows[designId] = new PrestigeSkinTable { Id = designId, CostumeId = costumeId };
            if (!DesignIds.Contains(designId)) DesignIds.Add(designId);
        }

        public static PrestigeSkinTable FindRow(int designId)
            => Rows.TryGetValue(designId, out var row) ? row : null;

        /// <summary>
        /// 原版行为：只返回「玩家买过的」皮肤。刻意不看 SkinDict ——
        /// 否则插件往字典里注入条目也会让这个方法的返回值变多，
        /// 就分不清到底是注入还是补丁在起作用了。
        /// </summary>
        public static List<PrestigeSkinDBInfo> ConnectedSkins(CostumeDBInfo costume)
        {
            var list = new List<PrestigeSkinDBInfo>();
            foreach (var designId in costume.OwnedDesigns)
            {
                if (SkinDict.TryGetValue(designId, out var skin)) list.Add(skin);
            }
            return list;
        }

        /// <summary>模拟点击皮肤：原版会向服务器发请求，这里只记一次调用。</summary>
        public static void SetSkin(int designId, bool isSet, Action callback)
        {
            SetSkinCalls++;
            callback?.Invoke();
        }

        public static void AddPrestigeSkinDBInfo(PrestigeSkinDBInfo info, bool fromServer)
        {
            if (info != null) SkinDict[info.CostumeDesignId] = info;
        }
    }
}
