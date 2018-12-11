#include "LIB\April\April_one.h"
#include "LIB\February\February_one.h"
#include "LIB\January\January_one.h"
#include "LIB\March\March_one.h"
#include "LIB\May\May_one.h"
#include "one.h"
#include <iostream>

int main()
{
    std::cout << "gate exe" <<std::endl;
    Aprilone();
	Februaryone();
	Januaryone();
	Marchone();
	Mayone();
	std::cout << one() <<std::endl;
    std::cin.get();
}