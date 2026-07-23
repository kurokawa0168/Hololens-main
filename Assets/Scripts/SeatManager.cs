using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SeatManager : MonoBehaviour
{
    public string GetSeatStatus(string seatId)
    {
        if (seatId == "A01") return "Green";
        if (seatId == "A02") return "Red";
        return "Yellow";
    }
}