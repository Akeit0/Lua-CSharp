local function add(a, b, c)
  local d = a + b + c
  return d
end

local x = 2
local y = 3

for i = 1, 10, 3 do
  local z = add(x, y ,i)
  print("x + y + i:", z)
end


print("end")