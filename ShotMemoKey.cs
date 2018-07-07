namespace WeaponVariance
{
    public struct ShotMemoKey
    {
        public int weaponEffectId;
        public int hitIndex;

        public ShotMemoKey(int weaponEffectId, int hitIndex)
        {
            this.weaponEffectId = weaponEffectId;
            this.hitIndex = hitIndex;
        }

        // This is fucking stupid, c#. The rest of this struct is fucking stupid. Fuck you.
        public override bool Equals(System.Object obj) 
        {
            return obj is ShotMemoKey && this == (ShotMemoKey)obj;
        }
        public override int GetHashCode() 
        {
            return weaponEffectId.GetHashCode() ^ hitIndex.GetHashCode();
        }
        public static bool operator ==(ShotMemoKey x, ShotMemoKey y) 
        {
            return x.hitIndex == y.hitIndex && x.weaponEffectId == y.weaponEffectId;
        }
        public static bool operator !=(ShotMemoKey x, ShotMemoKey y) 
        {
            return !(x == y);
        }
    }
}