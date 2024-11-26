local a = function(a) coroutine.wrap(a)(a) end
print( pcall(a, a))