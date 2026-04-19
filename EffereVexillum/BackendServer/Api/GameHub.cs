using Microsoft.AspNetCore.SignalR;

namespace BackendServer.Api;

public class GameHub : Hub
{
    public async Task JoinLobbyGroup(string lobbyId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);
    }
    
    public async Task LeaveLobbyGroup(string lobbyId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, lobbyId);
    }
}
