using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api")]
public class MessagesController(ChatService chat) : ControllerBase
{
    /// <summary>History, oldest -> newest. Pass before=&lt;createdAt of the oldest loaded message&gt; to page backwards.</summary>
    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<ActionResult<List<MessageDto>>> List(Guid id, [FromQuery] DateTime? before, [FromQuery] int limit = 50) =>
        await chat.GetMessagesAsync(id, User.GetUserId(), before, limit);

    /// <summary>REST fallback of the hub method SendMessage (same logic, same broadcast).</summary>
    [HttpPost("conversations/{id:guid}/messages")]
    public async Task<ActionResult<MessageDto>> Send(Guid id, SendMessageRequest req) =>
        await chat.SendMessageAsync(id, User.GetUserId(), req.Text, req.ReplyToId);

    [HttpPost("conversations/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        await chat.MarkReadAsync(id, User.GetUserId());
        return NoContent();
    }

    [HttpPut("messages/{id:guid}")]
    public async Task<ActionResult<MessageDto>> Edit(Guid id, EditMessageRequest req) =>
        await chat.EditMessageAsync(id, User.GetUserId(), req.Text);

    [HttpDelete("messages/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await chat.DeleteMessageAsync(id, User.GetUserId());
        return NoContent();
    }

    /// <summary>Toggle / replace my reaction on a message.</summary>
    [HttpPut("messages/{id:guid}/reaction")]
    public async Task<ActionResult<List<ReactionDto>>> React(Guid id, ReactRequest req) =>
        await chat.ToggleReactionAsync(id, User.GetUserId(), req.Emoji);
}
