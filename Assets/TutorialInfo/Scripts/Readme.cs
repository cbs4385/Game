#if UNITY_5_3_OR_NEWER
using System;
using UnityEngine;

public class Readme : ScriptableObject
{
    public Texture2D icon;
    public string title;
    public Section[] sections;
    public bool loadedLayout;

    [Serializable]
    public class Section
    {
        public string heading, text, linkText, url;
    }
}
#endif // UNITY_5_3_OR_NEWER
