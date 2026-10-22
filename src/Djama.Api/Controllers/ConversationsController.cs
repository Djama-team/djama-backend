using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api/conversations")]
public class ConversationsController(ChatService chat) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ConversationDto>>> List() => await chat.ListConversationsAsync(User.GetUserId());

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConversationDto>> Get(Guid id) => await chat.GetConversationAsync(id, User.GetUserId());

    /// <summary>Get the existing direct chat with a user or create it.</summary>
    [HttpPost("direct")]
    public async Task<ActionResult<ConversationDto>> Direct(UserIdRequest req) =>
        await chat.GetOrCreateDirectAsync(User.GetUserId(), req.UserId);

    [HttpPost("group")]
    public async Task<ActionResult<ConversationDto>> CreateGroup(CreateGroupRequest req) =>
        await chat.CreateGroupAsync(User.GetUserId(), req.Title, req.MemberIds);

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ConversationDto>> Rename(Guid id, RenameRequest req) =>
        await chat.RenameGroupAsync(id, User.GetUserId(), req.Title);

    [HttpPost("{id:guid}/members")]
    public async Task<ActionResult<ConversationDto>> AddMember(Guid id, UserIdRequest req) =>
        await chat.AddMemberAsync(id, User.GetUserId(), req.UserId);

    /// <summary>Admins remove anybody; any member can remove themselves (= leave the group).</summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        await chat.RemoveMemberAsync(id, User.GetUserId(), userId);
        return NoContent();
    }

    [HttpPut("{id:guid}/members/{userId:guid}/role")]
    public async Task<ActionResult<ConversationDto>> ChangeRole(Guid id, Guid userId, ChangeRoleRequest req) =>
        await chat.ChangeRoleAsync(id, User.GetUserId(), userId, req.Role);
}
