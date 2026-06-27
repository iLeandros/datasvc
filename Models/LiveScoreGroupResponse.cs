using System;
using System.Collections.Generic;
using System.Text;

namespace DataSvc.Models;

public class LiveScoreGroupResponse
{
    public string Competition { get; set; }
    public List<LiveScoreItemResponse> Matches { get; set; }
}
