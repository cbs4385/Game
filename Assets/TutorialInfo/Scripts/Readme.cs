using System;

#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

public class Readme
#if UNITY_5_3_OR_NEWER
    : ScriptableObject
#endif
{
#if UNITY_5_3_OR_NEWER
    public Texture2D icon;
#else
    public object icon;
#endif
    public string title;
    public Section[] sections;
    public bool loadedLayout;

    [Serializable]
    public class Section
    {
        public string heading, text, linkText, url;
    }
}
