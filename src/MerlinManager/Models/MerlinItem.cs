namespace MerlinManager.Models
{
    public class MerlinItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int ParentId { get; set; }
        public int Order { get; set; }
        public int NbChildren { get; set; }
        public int FavOrder { get; set; }
        public int Type { get; set; }
        public uint LimitTime { get; set; }
        public uint AddTime { get; set; }
        public string Uuid { get; set; } = string.Empty;

        // Type d'item selon la spécification Merlin
        // Note: type%32 donne le type de base
        public bool IsRoot => Type == 1;
        public bool IsDirectory => (Type % 32) == 2 || (Type % 32) == 6;
        public bool IsSound => (Type % 32) == 4;
        public bool IsFavoriteFolder => (Type % 32) == 10;
        public bool IsRecentFolder => (Type % 32) == 18;

        public MerlinItem Clone()
        {
            return new MerlinItem
            {
                Id = this.Id,
                Title = this.Title,
                ParentId = this.ParentId,
                Order = this.Order,
                NbChildren = this.NbChildren,
                FavOrder = this.FavOrder,
                Type = this.Type,
                LimitTime = this.LimitTime,
                AddTime = this.AddTime,
                Uuid = this.Uuid
            };
        }
    }
}
