-- Example MuGlyph Lua script.
-- Loaded per-world; the sandbox exposes: world, output, trigger, alias, timer, gmcp, log.
-- (No io/os.execute/require — scripting is sandboxed.)

output.print("Loaded example.lua for world: " .. world.name)

-- Trigger: callback receives the whole match followed by each capture group.
trigger.add("(%w+) waves at you", function(whole, who)
    world.send("wave " .. who)
    output.print("Waved back at " .. who)
end)

-- Alias: string form with $1..$9 capture references.
alias.add("^gt (.+)", "grouptell $1")

-- Alias: function form (same call shape as triggers).
alias.add("^hp$", function()
    world.send("score")
end)

-- Timer: recurring; returns a handle with :cancel().
local keepalive = timer.every(60000, function()
    world.send("")  -- blank line keeps some servers from idling us out
end)

-- Timer: one-shot.
timer.after(2000, function()
    output.print("Two seconds since load.")
end)

-- GMCP: a handler registered for "Char" also fires for "Char.Vitals", etc.
gmcp.on("Char.Vitals", function(json)
    output.print("Vitals update: " .. json)
end)

log.info("example.lua ready")
