local x = 0
local a =add
for _ = 0, 25000 do
    x = a(x, 1)
end

return x