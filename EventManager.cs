using UnityEngine;
using UnityEngine.Events;
using System.Collections;
using System.Collections.Generic;
using System;

public class EventManager : MonoBehaviour
{
    private static EventManager m_instance = null;
    public static EventManager Instance
    {
        get
        {
            if (!m_instance)
            {
                m_instance = FindObjectOfType(typeof(EventManager)) as EventManager;

                if (!m_instance)
                    return null;
                else
                    m_instance.init();
            }

            return m_instance;
        }
    }

    private Dictionary<string, List<Action<object>>> m_dict_function_event;

    private void init()
    {
        if (m_dict_function_event == null)
            m_dict_function_event = new Dictionary<string, List<Action<object>>>();
    }

    public static void StartListening(string strEventName, Action<object> unityActionListener)
    {
        List<Action<object>> thisEvent = null;
        if (Instance.m_dict_function_event.TryGetValue(strEventName, out thisEvent))
        {
            if(!thisEvent.Contains(unityActionListener))
                thisEvent.Add(unityActionListener);
        }
        else
        {
            thisEvent = new List<Action<object>>();
            thisEvent.Add(unityActionListener);
            Instance.m_dict_function_event.Add(strEventName, thisEvent);
        }
    }

    public static void StopListening(string strEventName, Action<object> unityActionListener)
    {
        if (m_instance == null)
        {
            //DebugLogger.LogError("[ERROR] : failed call function ---- StopListening! m_instance == null;");
            return;
        }

        List<Action<object>> thisEvent = null;
        if (Instance.m_dict_function_event.TryGetValue(strEventName, out thisEvent))
        {
            if (thisEvent.Contains(unityActionListener))
                thisEvent.Remove(unityActionListener);
        }
    }

    internal void StartListening(object setUpEffectSweepLight)
    {
        throw new NotImplementedException();
    }

    public static void TriggerEvent(string strEventName, object data = null)
    {
        List<Action<object>> thisEvent = null;
        if (Instance.m_dict_function_event.TryGetValue(strEventName, out thisEvent))
        {
            //foreach(Action<object> action in thisEvent)
            //    action.Invoke(data);
            for (int i = 0; i < thisEvent.Count; i++)
            {
                thisEvent[i].Invoke(data);
            }
        }
        //else
        //    DebugLogger.LogWarning("[ERROR] : failed call function ---- TriggerEvent; strEventName = " + strEventName);
    }

    //public static void TriggerEvent(string strEventName, object data = null)
    //{
    //    List<Action<object>> thisEvent = null;
    //    if (Instance.m_dict_function_event.TryGetValue(strEventName, out thisEvent))
    //    {
    //        //foreach(Action<object> action in thisEvent)
    //        //    action.Invoke(data);
    //        for (int i = 0; i < thisEvent.Count; i++)
    //        {

    //            if (isCheckTarget(strEventName, thisEvent[i].Target))
    //            {

    //                thisEvent[i].Invoke(data);
    //            }
    //            else
    //            {
    //                thisEvent.RemoveAt(i);
    //                i--;
    //        }
    //    }
    //    }
    //    //else
    //    //    DebugLogger.LogWarning("[ERROR] : failed call function ---- TriggerEvent; strEventName = " + strEventName);
    //}


    //private static bool isCheckTarget(string strEventName, object objData)
    //{
    //    bool bFounded = true;
    //    switch (strEventName)
    //    {
    //        case DP_LE_LevelEditor.Core.RLEManager.STR_TRIGGER_EVENT_DELETE:
    //            {
    //                DP_LE_LevelEditor.Core.RLEController rle = (DP_LE_LevelEditor.Core.RLEController)objData;
    //                if(rle && rle.gameObject && rle.gameObject.activeSelf)
    //                    bFounded = true;
    //                else
    //                    bFounded = false;
    //            }
    //            break;
    //        default:
    //            break;
    //    }

    //    return bFounded;
    //}
}