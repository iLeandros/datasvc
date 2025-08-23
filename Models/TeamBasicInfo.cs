using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataSvc.Models;

public class TeamBasicInfo
{
    public string? TeamName { get; set; }
    public string? TeamFlag { get; set; }
    public string? Country { get; set; }
    public string? CountryFlag { get; set; }
}
