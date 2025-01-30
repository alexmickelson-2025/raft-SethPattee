
public class HttpRpcOtherNode : ITransport
{
    public int Id { get; }
    public string Url { get; }
    private HttpClient client = new();

    public HttpRpcOtherNode(int id, string url)
    {
        Id = id;
        Url = url;
    }

    //public async Task RequestAppendEntries(AppendEntriesData request)
    //{
    //    try
    //    {
    //        await client.PostAsJsonAsync(Url + "/request/appendEntries", request);
    //    }
    //    catch (HttpRequestException)
    //    {
    //        Console.WriteLine($"node {Url} is down");
    //    }
    //}

    //public async Task RequestVote(VoteRequestData request)
    //{
    //    try
    //    {
    //        await client.PostAsJsonAsync(Url + "/request/vote", request);
    //    }
    //    catch (HttpRequestException)
    //    {
    //        Console.WriteLine($"node {Url} is down");
    //    }
    //}

    //public async Task RespondAppendEntries(RespondEntriesData response)
    //{
    //    try
    //    {
    //        await client.PostAsJsonAsync(Url + "/response/appendEntries", response);
    //    }
    //    catch (HttpRequestException)
    //    {
    //        Console.WriteLine($"node {Url} is down");
    //    }
    //}

    //public async Task ResponseVote(VoteResponseData response)
    //{
    //    try
    //    {
    //        await client.PostAsJsonAsync(Url + "/response/vote", response);
    //    }
    //    catch (HttpRequestException)
    //    {
    //        Console.WriteLine($"node {Url} is down");
    //    }
    //}

    //public async Task SendCommand(ClientCommandData data)
    //{
    //    await client.PostAsJsonAsync(Url + "/request/command", data);
    //}

    public Task<AppendEntriesResponse> SendAppendEntriesAsync(AppendEntries entries, string recipientNodeId)
    {
        throw new NotImplementedException();
    }

    public Task<bool> SendVoteRequestAsync(VoteRequest request, string recipientNodeId)
    {
        throw new NotImplementedException();
    }

    public Task SendAppendEntriesResponseAsync(AppendEntriesResponse response, string recipientNodeId)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<string> GetOtherNodeIds(string currentNodeId)
    {
        throw new NotImplementedException();
    }
}
