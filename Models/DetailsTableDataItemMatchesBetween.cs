namespace DataSvc.Models;

    public DetailsTableDataItemMatchesBetween(Match match, Details details)
    {
        this.sMatch = match;
        this.sDetails = details;
    }
    public Match sMatch { get; set; }
    public Details sDetails { get; set; }
}
