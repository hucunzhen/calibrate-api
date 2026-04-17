#ifndef PREPROCESS_H
#define PREPROCESS_H

/*************************************************
*
*             2D算法库预处理函数头文件
*           Created on 2022.5.31 by wangxinping
*
*************************************************/

#include "XVBase.h"
#include"XV2DBase.h"
#include<string>
namespace XVL
{
	//----------------------------------------------------------------------滤波算子----------------------------------------------------------------------

	typedef struct XVGaussianSmoothIn//高斯滤波平滑函数输入参数
	{
		XVImage* inImage;//输入图像
		XVRegion inRegion;//输入区域
		int inKernelWidth;//内核宽度，其内核的宽度必须为正数和奇数或者为0
		int inKernelHeight;//内核高度，其内核的高度都必须为正数和奇数或者为0
		float inStdDevX;//高斯核函数在X方向的标准偏差
		float inStdDevY;//高斯核函数在Y方向的标准偏差
	}XVGaussianSmoothIn;

	typedef struct XVGaussianSmoothOut//高斯滤波平滑函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outImage;//输出结果图像
		float outTime;//耗时
	}XVGaussianSmoothOut;

	//typedef struct XVMedianSmoothIn//中值滤波平滑函数输入参数
	//{
	//	XVImage* inImage;//输入图像
	//	XVRegion inRegion;//输入区域
	//	int inKernel;//孔径的线性尺寸，注意这个参数必须是大于1的奇数,默认值为3
	//}XVMedianSmoothIn;

	//typedef struct XVMedianSmoothOut//中值滤波平滑函数输出参数
	//{
	//	int outResult;//函数执行结果，1为OK，2为NG
	//	XVImage* outImage;//输出结果图像
	//	float outTime;//耗时
	//}XVMedianSmoothOut;

	typedef struct XVMedianSmoothIn {
		XVImage* inImage;//输入图像
		XVRegion inRegion;//输入区域
		int inRadiusX;    //核宽半径（核宽为2*inRadiusX+1）
		int inRadiusY;    //核高半径（核高为2*inRadiusX+1）

	}XVMedianSmoothIn;

	typedef struct XVMedianSmoothOut {
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outImage;//输出结果图像
		float outTime;//耗时

	}XVMedianSmoothOut;

	typedef struct XVMeanSmoothIn//均值滤波平滑函数输入参数
	{
		XVImage* inImage;//输入图像
		XVRegion inRegion;//输入区域
		int inKernelWidth;//内核宽度，其内核的宽度必须为正数和奇数或者为0
		int inKernelHeight;//内核高度，其内核的高度都必须为正数和奇数或者为0
		XVPoint2DInt inAnchor;//表示锚点（即被平滑的那个点），注意它有默认值XVPoint2DIntPoint（-1，-1），如果这个点坐标是负值，就表示取核的中心为锚点，所以默认值XVPoint2DInt（-1，-1）表示这个锚点在核的中心
	}XVMeanSmoothIn;

	typedef struct  XVMeanSmoothOut//均值滤波平滑函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outImage;//输出结果图像
		float outTime;//耗时
	} XVMeanSmoothOut;

	//----------------------------------------------------------------------空间转换算子----------------------------------------------------------------------

	typedef struct XVRgbToGrayIn//Rgb转灰度函数输入参数
	{
		XVImage* inRgbImage;
	}XVRgbToGrayIn;

	typedef struct XVRgbToGrayOut//Rgb转灰度函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outGrayImage;//输出图像
		float outTime;//耗时
	}XVRgbToGrayOut;

	typedef struct XVRgbToHsvIn//Rgb转Hsv函数输入参数
	{
		XVImage* inRgbImage;
	}XVRgbToHsvIn;

	typedef struct XVRgbToHsvOut//Rgb转Hsv函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outHsvImage;//输出图像
		float outTime;//耗时
	}XVRgbToHsvOut;

	typedef struct XVHsvToRgbIn//Hsv转Rgb函数输入参数
	{
		XVImage* inHsvImage;
	}XVHsvToRgbIn;

	typedef struct XVHsvToRgbOut//Hsv转Rgb函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outRgbImage;//输出图像
		float outTime;//耗时
	}XVHsvToRgbOut;

	typedef struct XVRgbToYuvIn//Rgb转Yuv图像输入参数
	{
		XVImage* inRgbImage;
	}XVRgbToYuvIn;

	typedef struct XVRgbToYuvOut//Rgb转Yuv函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outYuvImage;//输出图像
		float outTime;//耗时
	}XVRgbToYuvOut;
	typedef struct XVBgrToYuvIn//Bgr转Yuv函数输入参数
	{
		XVImage* inRgbImage;
	}XVBgrToYuvIn;

	typedef struct XVBgrToYuvOut//Bgr转Yuv函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outYuvImage;//输出图像
		float outTime;//耗时
	}XVBgrToYuvOut;


	typedef struct XVYuvToRgbIn//Yuv转Rgb函数输入参数
	{
		XVImage* inYuvImage;
	}XVYuvToRgbIn;

	typedef struct XVYuvToRgbOut//Yuv转Rgb函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outRgbImage;//输出图像
		float outTime;//耗时
	}XVYuvToRgbOut;


	typedef struct XVRgbToHsiIn//Rgb转Hsi输入参数
	{
		XVImage inImage;//输入图像
	}XVRgbToHsiIn;

	typedef struct XVRgbToHsiOut//Rgb转Hsi输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
		float outTime;//函数执行耗时
	}XVRgbToHsiOut;

	typedef struct XVRgbToHslIn//Rgb转Hsl输入参数
	{
		XVImage* inRgbImage;;//输入图像
	}XVRgbToHslIn;

	typedef struct XVRgbToHslOut//Rgb转Hsl输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outHsvImage;//输出图像
		float outTime;//耗时
	}XVRgbToHslOut;


	typedef struct XVHsiToRgbIn//Hsi转Rgb输入参数
	{
		XVImage inImage;//输入图像
	}XVHsiToRgbIn;

	typedef struct XVHsiToRgbOut//Hsi转Rgb输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
		float outTime;//函数执行耗时
	}XVHsiToRgbOut;

	typedef struct XVRgbToMeanGrayIn//Rgb转平均灰度函数输入参数
	{
		XVImage inImage;
	}XVRgbToMeanGrayIn;

	typedef struct XVRgbToMeanGrayOut//Rgb转平均灰度函数输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
		float outTime;//耗时
	}XVRgbToMeanGrayOut;

	typedef struct XVRgbToWeightedGrayIn {    //Rgb图像通道加权平均创建单通道图像
		XVImage* inImage;
		XVRegion inRoi;
		int inWeight1;               //1通道权重
		int inWeight2;               //2通道权重
		int inWeight3;               //3通道权重
	}XVRgbToWeightedGrayIn;

	typedef struct XVRgbToWeightedGrayOut {
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
		float outTime;//耗时
	}XVRgbToWeightedGrayOut;

	typedef struct XVRgbAddChannelsIn {
		XVImage* inImage;
		XVRegion inRoi;

	}XVRgbAddChannelsIn;

	typedef struct XVRgbAddChannelsOut {
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
		float outTime;//耗时
	}XVRgbAddChannelsOut;

	//----------------------------------------------------------------------旋转镜像----------------------------------------------------------------------


	typedef struct XVMirrorImageIn//镜像图像输入参数
	{
		XVImage* inImage;//输入图像
		MirrorDirection inMirrorDirection;//镜像方向
	}XVMirrorImageIn;

	typedef struct XVMirrorImageOut//镜像图像输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outImage;//输出图像
		float outTime;//耗时
	}XVMirrorImageOut;



	typedef struct XVRotateImageIn//旋转图像输入参数
	{
		XVImage* inImage;//输入图像
		RotateAngle inRotateAngle;//旋转角度
	}XVRotateImageIn;

	typedef struct XVRotateImageOut//旋转图像输出参数
	{
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage* outImage;//输出图像
		float outTime;//耗时
	}XVRotateImageOut;

	//----------------------------------------------------------------------其他预处理----------------------------------------------------------------------
	
	typedef struct XVBrightnessCorrectionIn//亮度校正输入参数
	{
		XVImage inImage;//输入图像
		int inGain;//增益，调节该系数会使得图像画面整体像素亮度提高，默认值为0，调节范围0-100
		int inCompensation;//补偿，调节该系数使得画面的像素整体加或者减该数值，默认值为0，调节范围-255到255
	}XVBrightnessCorrectionIn;

	
	typedef struct XVBrightnessCorrectionOut//亮度校正输出参数
	{
		int outResult;//执行结果，1代表函数执行成功，2代表函数执行失败
		XVImage outImage;//亮度校正后的结果图像
		float outTime;//函数执行耗时
	}XVBrightnessCorrectionOut;


	
	typedef struct XVCopyAndFillIn//图像裁剪输入参数
	{
		XVImage inImage;//输入图像
		XVRegion inRoi;//感兴趣区域，若区域为空，则默认全图
		CopyAndFillType inCopyAndFillType;//拷贝填充处理类型，主要分拷贝和填充两种类型
		int inRegionInnerGrayValue;//区域内填充值，范围0-255
		int inRegionOuterGrayValue;//区域外填充值，范围0-255
		//bool isOpenAlignment;//是否启用局部坐标系
		XVCoordinateSystem2D alignment;//局部坐标系参数
		SizeMode inSizeMode;//默认值Preserve
	}XVCopyAndFillIn;

	typedef struct XVCopyAndFillOut//图像裁剪算子输出参数
	{
		int outResult;//执行结果，1代表函数执行成功，2代表函数执行失败
		XVRegion outRegion;//roi区域(若启用局部坐标系，则是roi变换后的区域)
		XVImage outImage;//输出图像
		float outTime;//函数执行耗时
	}XVCopyAndFillOut;

	typedef struct XVMultipleImageMeanIn//帧平均输入参数
	{
		std::vector<XVImage> inImages;//输入图像
		XVRectangle2D inRoi;//感兴趣区域
	}XVMultipleImageMeanIn;
	
	typedef struct XVMultipleImageMeanOut//帧平均输出参数
	{
		int outResult;//执行结果，1代表函数执行成功，2代表函数执行失败
		XVImage outImage;//输出图像
		float outTime;//函数执行耗时
	}XVMultipleImageMeanOut;

	typedef struct xvColorParam
	{
		float H_min;//H最小值
		float H_max;//H最大值
		float S_min;//S最小值
		float S_max;//S最大值
		float V_min;//V最小值
		float V_max;//V最大值
		std::string colorName;//颜色名称
	}xvColorParam;

	//颜色识别(HSV)输入参数
	typedef struct XVHsvColorDiscriminationIn
	{
		XVImage* inRgbImage;//输入彩色图像
		XVRegion inRoi;//感兴趣区域，关闭软件后感兴趣区域需保存下来
		vector<xvColorParam> inColorOption;//具体参数可参考EDU版本，有11种初始化颜色类型
	}XVHsvColorDiscriminationIn;

	//颜色识别(HSV)输出参数
	typedef struct XVHsvColorDiscriminationOut
	{
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		std::string outColorName;//输出颜色名称
	}XVHsvColorDiscriminationOut;

	//颜色通道分离输入参数
	typedef struct XVSplitChannelsIn
	{
		XVImage inImage;//输入三通道图像
	}XVSplitChannelsIn;

	//颜色通道分离输出参数
	typedef struct XVSplitChannelsOut
	{
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage1;//第一通道图像
		XVImage outImage2;//第二通道图像
		XVImage outImage3;//第三通道图像
	}XVSplitChannelsOut;

	//图像gamma变换输入参数
	typedef struct XVGammaTransformIn
	{
		XVImage inImage;//输入图像
		XVRegion inRoi;
		float inGamma;//gamma值，默认值为1，取值范围大于0
	}XVGammaTransformIn;

	//图像gamma变换输出参数
	typedef struct XVGammaTransformOut
	{
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
	}XVGammaTransformOut;

	//图像锐化输入参数
	typedef struct XVImageSharpenIn
	{
		XVImage inImage;//输入图像
		int inSharpenParam;//锐化系数，默认值1，取值范围0-100
		int inKernelWidth;//核宽大小,默认值3，取值范围0-图像宽度
		int inKernelHeight;//核高大小，默认值值3，取值范围0-图像高度
	}XVImageSharpenIn;

	//图像锐化输出参数
	typedef struct XVImageSharpenOut
	{
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
	}XVImageSharpenOut;

	//图像翻转输入参数
	typedef struct XVImageInvertIn
	{
		XVImage inImage;//输入图像
	}XVImageInvertIn;

	//图像翻转输出参数
	typedef struct XVImageInvertOut
	{
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
	}XVImageInvertOut;

	
	//像素指数给定的功率
	typedef struct XVPowerImageIn {
		XVImage inImage;//输入图像
		XVRegion inRoi;
		float inValue;  //指数(range：0-999999)(df:1)

	}XVPowerImageIn;

	typedef struct XVPowerImageOut {
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
	}XVPowerImageOut;

	typedef struct XVImagePixelIn {
		XVImage* inImage;//输入图像
		XVRegion inRoi;

	}XVImagePixelIn;

	typedef struct XVImagePixelOut {
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVPixel* outPixels;//输出像素值
		int length;    //输出像素的一维数组长度
	}XVImagePixelOut;


	typedef struct XVDilateImageIn {
		XVImage* inImage;//输入图像
		XVRegion inRoi;
		XVMorphShape inKernel = XVMorphShape::Rect;  //核类型
		BorderType inType; //边界类型：拷贝填充镜像
		XVPixel inBorderColor;            //填充的像素
		int inRadiusX = 1;    //核宽半径（核宽为2*inRadiusX+1）
		int inRadiusY = 1;    //核高半径（核高为2*inRadiusX+1）
	}XVDilateImageIn;

	typedef struct XVDilateImageOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVDilateImageOut;

	typedef struct XVErodeImageIn {
		XVImage* inImage;//输入图像
		XVRegion inRoi;
		XVMorphShape inKernel = XVMorphShape::Rect;  //核类型
		BorderType inType;//边界类型：拷贝填充镜像
		XVPixel inBorderColor;            //填充的像素
		int inRadiusX = 1;    //核宽半径（核宽为2*inRadiusX+1）
		int inRadiusY = 1;    //核高半径（核高为2*inRadiusX+1）
	}XVErodeImageIn;

	typedef struct XVErodeImageOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVErodeImageOut;

	typedef struct XVCloseImageIn {
		XVImage inImage;//输入图像
		XVRegion inRoi;
		XVMorphShape inKernel = XVMorphShape::Rect;  //核类型
		BorderType inType;//边界类型：拷贝填充镜像
		XVPixel inBorderColor;            //填充的像素
		int inRadiusX = 1;    //核宽半径（核宽为2*inRadiusX+1）
		int inRadiusY = 1;    //核高半径（核高为2*inRadiusX+1）
	}XVCloseImageIn;

	typedef struct XVCloseImageOut {
		float outTime;//函数耗时
		int outResult;//函数执行结果，1为OK，2为NG
		XVImage outImage;//输出图像
	}XVCloseImageOut;

	typedef struct XVOpenImageIn {
		XVImage* inImage;//输入图像
		XVRegion inRoi;
		XVMorphShape inKernel = XVMorphShape::Rect;  //核类型
		BorderType inType;//边界类型：拷贝填充镜像
		XVPixel inBorderColor;            //填充的像素
		int inRadiusX = 1;    //核宽半径（核宽为2*inRadiusX+1）
		int inRadiusY = 1;    //核高半径（核高为2*inRadiusX+1）
	}XVOpenImageIn;

	typedef struct XVOpenImageOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVOpenImageOut;

	typedef struct XVDrawStringsIn {
		XVImage* inImage;//输入图像
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		vector<string> inStrings;     //输入字符串数组
		vector<XVPoint2D> inCoordinates;      //输入字符串对应位置坐标数组
		vector <XVPixel> inColor;              //字符串颜色数组
		int inStringsSize;   //字符串大小
		float inAngle;       //字符串角度
		bool   isToRGB;      //是否转换为RGB图像
	}XVDrawStringsIn;

	typedef struct XVDrawStringsOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVDrawStringsOut;

	typedef struct XVDrawStringsSingleColorIn {
		XVImage* inImage;//输入图像
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		vector<string> inStrings;     //输入字符串数组
		vector<XVPoint2D> inCoordinates;      //输入字符串对应位置坐标数组
		XVPixel inColor;              //字符串颜色
		int inStringsSize;   //字符串大小
		float inAngle;       //字符串角度
		bool   isToRGB;      //是否转换为RGB图像
	}XVDrawStringsSingleColorIn;

	typedef struct XVDrawStringsSingleColorOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVDrawStringsSingleColorOut;

	typedef struct XVDrawStringsTwoColorIn {
		XVImage* inImage;//输入图像
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		vector<string> inStrings;     //输入字符串数组
		vector<XVPoint2D> inCoordinates;      //输入字符串对应位置坐标数组
		XVPixel inColorTrue;              //判断为True时字符串颜色
		XVPixel inColorFalse;             //判断为False字符串颜色
		vector< bool> inConditions;       //判断数组
		int inStringsSize;   //字符串大小
		float inAngle;       //字符串角度
		bool   isToRGB;      //是否转换为RGB图像
	}XVDrawStringsTwoColorIn;

	typedef struct XVDrawStringsTwoColorOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVDrawStringsTwoColorOut;

	typedef struct XVDownsampleImageIn {
		XVImage* inImage;//输入图像
		int inScaleStep = 1; //指数(range：[0 - 12])(df:1)
		DownsampleFuction inFuction;    //
	}XVDownsampleImageIn;

	typedef struct XVDownsampleImageOut {
		float outTime;//函数耗时
		XVImage outImage;//输出图像
	}XVDownsampleImageOut;

	

	//彩色图像转灰度图像算子
	//Rgb转灰度图像
	_declspec(dllexport) void XVRgbToGray(XVRgbToGrayIn& rgbToGrayIn, XVRgbToGrayOut& rgbToGrayOut);
	//Rgb转平均灰度图像
	_declspec(dllexport) void XVRgbToMeanGray(XVRgbToMeanGrayIn& rgbToMeanGray, XVRgbToMeanGrayOut& rgbToMeanGrayOut);
	//Rgb通道加权平均转灰度
	_declspec(dllexport) void XVRgbToWeightedGray(XVRgbToWeightedGrayIn& XVRgbToWeightedGray_In, XVRgbToWeightedGrayOut& XVRgbToWeightedGray_Out);
	//Rgb通道饱和
	_declspec(dllexport) void XVRgbAddChannels(XVRgbAddChannelsIn& XVRgbAddChannels_In, XVRgbAddChannelsOut& XVRgbAddChannels_Out);
	//Rgb转Hsv图像
	_declspec(dllexport) void XVRgbToHsv(XVRgbToHsvIn& rgbToHsvIn, XVRgbToHsvOut& rgbToHsvOut);
	//Hsv转Rgb图像
	_declspec(dllexport) void XVHsvToRgb(XVHsvToRgbIn& hsvToRgbIn, XVHsvToRgbOut& hsvToRgbOut);
	//Rgb转Yuv图像
	_declspec(dllexport) void XVRgbToYuv(XVRgbToYuvIn& rgbToYuvIn, XVRgbToYuvOut& rgbToYuvOut);
	//Bgr转Yuv图像
	_declspec(dllexport) void XVBgrToYuv(XVBgrToYuvIn& bgrToYuvIn, XVBgrToYuvOut& bgrToYuvOut);
	//Yuv转Rgb图像
	_declspec(dllexport) void XVYuvToRgb(XVYuvToRgbIn& yuvToRgbIn, XVYuvToRgbOut& yuvToRgbOut);
	//Rgb转Hsi图像
	_declspec(dllexport) void XVRgbToHsi(XVRgbToHsiIn& rgbToHsiIn, XVRgbToHsiOut& rgbToHsiOut);
	//Rgb转Hsl图像
	_declspec(dllexport) void XVRgbToHsl(XVRgbToHslIn& rgbToHslIn, XVRgbToHslOut& rgbToHslOut);
	//Hsi转Rgb图像
	_declspec(dllexport) void XVHsiToRgb(XVHsiToRgbIn& hsiToRgbIn, XVHsiToRgbOut& hsiToRgbOut);

	//滤波算子
	//高斯滤波
	_declspec(dllexport) void XVGaussianSmooth(XVGaussianSmoothIn& gaussianSmoothIn, XVGaussianSmoothOut& gaussianSmoothOut);
	//中值滤波
	//_declspec(dllexport) void XVMedianSmooth(XVMedianSmoothIn& medianSmoothIn, XVMedianSmoothOut& medianSmoothOut);
	_declspec(dllexport) void XVMedianSmooth(XVMedianSmoothIn& medianSmoothIn, XVMedianSmoothOut& medianSmoothOut);
	//均值滤波
	_declspec(dllexport) void XVMeanSmooth(XVMeanSmoothIn& meanSmoothIn, XVMeanSmoothOut& meanSmoothOut);

	//图像镜像/旋转算子
	//图像镜像算子
	_declspec(dllexport) void XVMirrorImage(XVMirrorImageIn& mirrorImageIn, XVMirrorImageOut& mirrorImageOut);

	//图像旋转算子
	_declspec(dllexport) void XVRotateImage(XVRotateImageIn& rotateImageIn, XVRotateImageOut& rotateImageOut);

	//其他预处理算子
	//亮度校正(outGrayValue=(1+inGain*0.1)*inGrayValue+inCompsation)
	_declspec(dllexport) void XVBrightnessCorrection(XVBrightnessCorrectionIn& brightnessCorrectionIn, XVBrightnessCorrectionOut& brightnessCorrectionOut);

	//图像裁剪（区域）(不限制灰度图像还是彩色图像，功能类似于抠图)
	_declspec(dllexport) void XVCopyAndFill(XVCopyAndFillIn& copyAndFillIn, XVCopyAndFillOut& copyAndFillOut);

	//帧平均算子
	_declspec(dllexport) void XVMultipleImageMean(XVMultipleImageMeanIn& multipleImageMeanIn, XVMultipleImageMeanOut& multipleImageMeanOut);

	//颜色识别(HSV)算子
	_declspec(dllexport) void XVHsvColorDiscrimination(XVHsvColorDiscriminationIn& hsvColorDiscriminationIn, XVHsvColorDiscriminationOut& hsvColorDiscriminationOut);

	//颜色通道分离算子
	_declspec(dllexport) void XVSplitChannels(XVSplitChannelsIn& splitChannelsIn, XVSplitChannelsOut& splitChannelsOut);

	//图像gamma变换:γ>1时，图像的高灰度区域对比度得到增强，直观效果是一幅偏亮的图变暗了下来;γ< 1时，图像的低灰度区域对比度得到增强，直观效果是一幅偏暗的图变亮了起来。----增加了双线程加速
	_declspec(dllexport) void XVGammaTransform(XVGammaTransformIn& gammaTransformIn, XVGammaTransformOut& gammaTransformOut);

	//图像锐化（公式：res := round((origImage - meanImage) * inSharpenParam) + origImage，参考halcon）
	_declspec(dllexport) void XVImageSharpen(XVImageSharpenIn& imageSharpenIn, XVImageSharpenOut& imageSharpenOut);

	//图像翻转
	_declspec(dllexport) void XVImageInvert(XVImageInvertIn& imageInvertIn, XVImageInvertOut& imageInvertOut);

	//像素指数给定的功率
	_declspec(dllexport) void XVPowerImage(XVPowerImageIn& XVPowerImage_In, XVPowerImageOut& XVPowerImage_Out);

	//提取像素数组
	_declspec(dllexport) void XVImagePixel(XVImagePixelIn& XVImagePixel_In, XVImagePixelOut& XVImagePixel_Out);

	//图像膨胀
	_declspec(dllexport) int XVDilateImage(XVDilateImageIn& XVDilateImage_In, XVDilateImageOut& XVDilateImage_Out);

	//图像腐蚀
	_declspec(dllexport) int XVErodeImage(XVErodeImageIn& XVErodeImage_In, XVErodeImageOut& XVErodeImage_Out);

	//图像闭操作
	_declspec(dllexport) void XVCloseImage(XVCloseImageIn& XVCloseImage_In, XVCloseImageOut& XVCloseImage_Out);

	//图像开操作
	_declspec(dllexport) int XVOpenImage(XVOpenImageIn& XVOpenImage_In, XVOpenImageOut& XVOpenImage_Out);

	//图像上绘制字符串(多颜色)
	_declspec(dllexport) int XVDrawStrings(XVDrawStringsIn& XVDrawStrings_In, XVDrawStringsOut& XVDrawStrings_Out);

	//图像上绘制字符串(单颜色)
	_declspec(dllexport) int XVDrawStringsSingleColor(XVDrawStringsSingleColorIn& XVDrawStringsSingleColor_In, XVDrawStringsSingleColorOut& XVDrawStringsSingleColor_Out);

	//图像上绘制字符串(双颜色)
	_declspec(dllexport) int XVDrawStringsTwoColor(XVDrawStringsTwoColorIn& XVDrawStringsTwoColor_In, XVDrawStringsTwoColorOut& XVDrawStringsTwoColor_Out);

	//图像下采样
	_declspec(dllexport) int XVDownsampleImage(XVDownsampleImageIn& XVDownsampleImage_In, XVDownsampleImageOut& XVDownsampleImage_Out);
}

#endif // !PREPROCESS_H
