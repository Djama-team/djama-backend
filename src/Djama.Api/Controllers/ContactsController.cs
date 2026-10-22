using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api/contacts")]
public class ContactsController(DjamaDbContext db, ChatService chat) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ContactDto>>> List()
    {
        var me = User.GetUserId();
        var list = await db.Contacts.Where(c => c.OwnerId == me).Include(c => c.ContactUser)
            .OrderBy(c => c.ContactUser!.DisplayName).ToListAsync();
        return list.Select(c => new ContactDto(chat.ToUserDto(c.ContactUser!), c.CreatedAt)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ContactDto>> Add(UserIdRequest req)
    {
        var me = User.GetUserId();
        if (req.UserId == me) throw DomainException.Validation("You cannot add yourself");
        var target = await db.Users.FindAsync(req.UserId) ?? throw DomainException.NotFound("User not found");
        if (await chat.IsBlockedBetweenAsync(me, target.Id)) throw DomainException.Blocked();
        var contact = await db.Contacts.FindAsync(me, target.Id);
        if (contact == null)
        {
            contact = new Contact { OwnerId = me, ContactUserId = target.Id };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();
        }
        return new ContactDto(chat.ToUserDto(target), contact.CreatedAt);
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Remove(Guid userId)
    {
        var contact = await db.Contacts.FindAsync(User.GetUserId(), userId);
        if (contact != null) { db.Contacts.Remove(contact); await db.SaveChangesAsync(); }
        return NoContent();
    }
}

[ApiController, Authorize, Route("api/blocks")]
public class BlocksController(DjamaDbContext db, ChatService chat) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List()
    {
        var me = User.GetUserId();
        var list = await db.Blocks.Where(b => b.BlockerId == me).Include(b => b.BlockedUser).ToListAsync();
        return list.Select(b => chat.ToUserDto(b.BlockedUser!)).ToList();
    }

    /// <summary>Blocking also removes the user from my contacts. Direct messages are refused in both directions.</summary>
    [HttpPost]
    public async Task<ActionResult<UserDto>> Block(UserIdRequest req)
    {
        var me = User.GetUserId();
        if (req.UserId == me) throw DomainException.Validation("You cannot block yourself");
        var target = await db.Users.FindAsync(req.UserId) ?? throw DomainException.NotFound("User not found");
        if (await db.Blocks.FindAsync(me, target.Id) == null) db.Blocks.Add(new Block { BlockerId = me, BlockedUserId = target.Id });
        var contact = await db.Contacts.FindAsync(me, target.Id);
        if (contact != null) db.Contacts.Remove(contact);
        await db.SaveChangesAsync();
        return chat.ToUserDto(target);
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Unblock(Guid userId)
    {
        var block = await db.Blocks.FindAsync(User.GetUserId(), userId);
        if (block != null) { db.Blocks.Remove(block); await db.SaveChangesAsync(); }
        return NoContent();
    }
}
