using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class IFCKV : MonoBehaviour
{
    [Serializable]
    public struct KeyValueItem
    {
        public string Key;
        public string Value;
    }

    public List<KeyValueItem> attribs = new List<KeyValueItem>();

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
