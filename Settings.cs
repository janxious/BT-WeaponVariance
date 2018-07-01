using System.Collections.Generic;

namespace WeaponVariance
{
    public class Settings
    {
        public const string ModName = "WeaponVariance";
        public const string ModId   = "com.joelmeador.WeaponVariance";

        public bool debug = false;

        public string[] variancePerShot = { };
        public string[] VariancePerShot => variancePerShot;
    }
}